using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;


internal class TranslationOrchestrator
{
    private readonly AutoLLMConfig _config;
    private readonly TaskQueue _taskQueue;
    private readonly RetryHandler _retryHandler;
    private readonly RateLimitGuard _rateLimitGuard;
    private readonly ConversationHistory _history;
    private readonly ILlmClient _llmClient;
    private readonly GlossaryManager? _glossary;
    private readonly GlossaryWorker? _glossaryWorker;   // 术语抽取线程（双线程架构，仅 AutoGlossary=true）

    private volatile bool _shutdownRequested;
    private volatile int _processingCount;
    private int _batchSeq;
    private int _traceTurnCount;   // 对话历史已禁用，用本地计数替代 _history.TurnCount 用于调用轨迹首轮判定
    private Thread? _workerThread;
    private long _totalInputTokens, _totalOutputTokens;
    private long _totalCacheHitTokens, _totalCacheMissTokens;

    public TaskQueue Queue => _taskQueue;

    public TranslationOrchestrator(AutoLLMConfig config, ILlmClient llmClient)
    {
        _config = config;
        _llmClient = llmClient ?? new LlmClient();
        _taskQueue = new TaskQueue();
        _retryHandler = new RetryHandler(config.MaxRetry);
        _rateLimitGuard = new RateLimitGuard();
        _history = new ConversationHistory
        {
            // 双线程架构（翻译 + 术语抽取）下关闭对话历史：两线程各自对 LLM 调用彼此无状态依赖，
            // 消除共享历史带来的同步问题，并使前缀缓存命中随轮数不再衰减。
            // _history 现仅保留系统提示词基线与 AllocKeys/EstimateTokens 供 SelectBatch 使用。
            Enabled = false,
            MaxContext = config.MaxContext
        };

        // 术语表管理器（AutoGlossary=true 时启用）
        if (config.AutoGlossary)
        {
            _glossary = new GlossaryManager(config.GlossaryPath);
            // 翻译线程系统提示词基线（含初始术语表）。术语表后续增长由术语抽取线程按阈值合并，
            // 不再借对话历史清空事件驱动；翻译线程每批派发时从 _glossary 重建提示词即时生效。
            var fullPrompt = _glossary.BuildSystemPrompt(config.CachedTranslationPrompt);
            _history.InitSystemPrompt(fullPrompt);

            // 启动独立的术语抽取线程，与翻译线程共享同一 RateLimitGuard（共享退避）
            _glossaryWorker = new GlossaryWorker(config, _llmClient, _glossary, _rateLimitGuard);
        }
        else
        {
            _history.InitSystemPrompt(config.CachedSystemPrompt);
        }
    }

    // ---- 公开方法 ----

    /// <summary>启动后台工作线程（翻译线程；AutoGlossary 时一并启动术语抽取线程）。</summary>
    public void Start()
    {
        _workerThread = new Thread(WorkerLoop) { IsBackground = true };
        _workerThread.Start();
        _glossaryWorker?.Start();
    }

    /// <summary>请求关闭。</summary>
    public void Shutdown()
    {
        _shutdownRequested = true;
        try { _glossaryWorker?.Shutdown(); } catch { }
        try { _taskQueue.Signal.Set(); } catch { }
    }

    // ---- 工作线程主循环 ----

    private void WorkerLoop()
    {
        while (!_shutdownRequested)
        {
            // 等待新任务或 100ms 超时（兼作沉淀窗口的保底唤醒）
            _taskQueue.Signal.WaitOne(100);

            // 限速检查
            if (_rateLimitGuard.IsBlocked())
                continue;

            // 并发控制（ParallelCount 已废弃，固定单并发）
            if (_processingCount >= AutoLLMConfig.ParallelCount)
                continue;

            // 批次沉淀窗口（debounce 100ms）：队列非空时，等到距最近一次入队满 100ms 再取批。
            // 期间若又有新任务入队，LastEnqueueTick 被刷新，等待自动延后——
            // 把同一时刻附近逐帧到达的文本聚到一次 LLM 调用，避免单条碎片文本缺少上下文。
            if (_taskQueue.Count > 0)
            {
                int remaining = 100 - unchecked(Environment.TickCount - _taskQueue.LastEnqueueTick);
                if (remaining > 0)
                    continue;
            }

            // 积压告警
            int outstanding = _taskQueue.OutstandingCount;
            if (outstanding > 200)
                Logger.Warn("任务积压严重: " + outstanding + " 条");

            // 取批并调度
            DispatchBatches();
        }
    }

    private void DispatchBatches()
    {
        while (_processingCount < AutoLLMConfig.ParallelCount)
        {
            // 1. 取出所有兼容任务（无字符上限）
            var pending = _taskQueue.DequeueAll();
            if (pending.Count == 0)
                break;

            // 对话历史已禁用：不再做 CheckAndClearIfOverLimit 触发的合并。
            // 术语表合并改由术语抽取线程按 GlossaryMergeThreshold 阈值驱动。

            // 2. Token 感知任务选择（复杂超限处理封装在 SelectBatch 内）
            List<TranslationTask> batch, overflow;
            int estimatedTotal;
            if (!SelectBatch(pending, out batch, out overflow, out estimatedTotal))
                continue;   // 全部被丢弃 → 继续循环尝试下一批兼容任务

            // 4. 溢出任务归位（插入队首，优先于新到达）
            if (overflow.Count > 0)
            {
                _taskQueue.ReEnqueueFront(overflow);
                int overflowTotal = estimatedTotal + _history.EstimateTokens(overflow[0].UntranslatedText);
                Logger.Debug("批次截断: " + overflow.Count + " 条返回队首 | " +
                    "估算超限 " + overflowTotal + " > " + _config.MaxContext);
            }

            // 5. 标记并提交
            foreach (var task in batch)
                task.State = TaskState.Processing;
            _processingCount++;

            var capturedBatch = batch;
            ThreadPool.QueueUserWorkItem(_ => ProcessBatch(capturedBatch));
        }
    }

    /// <summary>
    /// 从 pending 列表中按 MaxContext 上限选取一批任务。
    /// 对话历史已禁用：容量基线为系统提示词（含当前术语表）常量，无"历史太满清空"分支。
    /// 超限任务的处理策略：
    ///   - batch 非空时超限 → 本条及后续全归 overflow（"下一句"截断逻辑）
    ///   - batch 为空时单条超 MaxContext → MarkFailed 丢弃
    /// </summary>
    /// <returns>true 表示 batch 非空（可提交）；false 表示全部被丢弃，应继续下一轮。</returns>
    private bool SelectBatch(List<TranslationTask> pending,
        out List<TranslationTask> batch, out List<TranslationTask> overflow,
        out int estimatedTotal)
    {
        batch = new List<TranslationTask>();
        overflow = new List<TranslationTask>();
        estimatedTotal = _history.TotalContextTokens;

        for (int i = 0; i < pending.Count; i++)
        {
            var task = pending[i];
            int taskEstimate = _history.EstimateTokens(task.UntranslatedText);
            int newTotal = estimatedTotal + taskEstimate;

            if (newTotal <= _config.MaxContext)
            {
                batch.Add(task);
                estimatedTotal = newTotal;
                continue;
            }

            // 超限分支：batch 非空 → 本条及后续全归 overflow
            if (batch.Count > 0)
            {
                overflow.Add(task);
                for (int j = i + 1; j < pending.Count; j++)
                    overflow.Add(pending[j]);
                return true;
            }

            // batch 为空时的超限处理
            if (taskEstimate > _config.MaxContext)
            {
                // 文本自身确实超限 → 丢弃
                // 对话历史已禁用：TotalContextTokens 即系统提示词基线，单条超 MaxContext 即自身超限
                task.MarkFailed("单条文本超出 MaxContext(" + _config.MaxContext + ")");
                _taskQueue.MarkCompleted();
                _history.IncrementDiscardCount();
                Logger.Warn("单条文本超出最大上下文(" + _config.MaxContext + "), 丢弃(第" +
                    _history.DiscardCount + "次) | 估算" + taskEstimate +
                    " tokens | 文本: " + Truncate(task.UntranslatedText, 80));
                estimatedTotal = _history.TotalContextTokens;   // 重置
                continue;
            }

            // 对话历史已禁用：不存在"历史太满"分支。newTotal 超限且 batch 为空且 taskEstimate
            // 未超 MaxContext 的情形在 Enabled=false 下不可达（baseline 为常量），故此处不再处理。
            // 兜底防死循环：将该条标记为超限丢弃并继续
            task.MarkFailed("无法纳入批次（超出 MaxContext 余量）: 估算" + newTotal + " > " + _config.MaxContext);
            _taskQueue.MarkCompleted();
            _history.IncrementDiscardCount();
            Logger.Warn("任务无法纳入批次（超出 MaxContext 余量）, 丢弃 | 估算" + newTotal +
                " tokens > " + _config.MaxContext + " | 文本: " + Truncate(task.UntranslatedText, 80));
            estimatedTotal = _history.TotalContextTokens;
        }

        return batch.Count > 0;
    }

    // ---- 批次处理 ----

    private void ProcessBatch(List<TranslationTask> batch)
    {
        int batchId = Interlocked.Increment(ref _batchSeq);
        bool isRateLimit = false;

        try
        {
            // 收集文本
            var texts = new List<string>();
            foreach (var task in batch)
                texts.Add(task.UntranslatedText);

            // 分配本批 JSON 编号键（"1"/"2"/...，全局单调递增；对话历史已禁用但 _nextKey
            // 仍单调递增，避免与术语线程/历史无关的跨批重号）
            _history.AllocKeys(batch);

            // 构建用户输入 JSON（键取自各 task.UserKey）
            string inputJson = BuildInputJson(batch);

            // 构建消息：术语表模式使用含术语表的翻译模式提示词（仅应用术语、不产出新术语）；
            // 对话历史已禁用 → BuildMessages 内仅返回 [system, user]，无历史条目。
            string systemPrompt = _config.AutoGlossary && _glossary != null
                ? _glossary.BuildSystemPrompt(_config.CachedTranslationPrompt)
                : _config.CachedSystemPrompt;
            var messages = _history.BuildMessages(systemPrompt, inputJson);

            // ---- 轨迹表头数据（调用前捕获） ----
            int totalChars = 0;
            foreach (var t in texts) totalChars += t.Length;
            long waitMs = Environment.TickCount - batch[0].CreatedTick;
            int ctxTokens = _history.TotalContextTokens;
            int turnCount = _traceTurnCount;
            bool firstTurn = (turnCount == 0);

            // 同步调用 LLM（阻塞 ThreadPool 线程，net35 下无可避免）
            LlmResult result = _llmClient.Translate(
                _config.EndpointUrl, _config.ApiKey, _config.Model,
                messages, _config.ParsedModelParams);

            _rateLimitGuard.Reset();

            // Token 统计（累积，并发安全：ProcessBatch 在 ThreadPool 上并行执行）
            Interlocked.Add(ref _totalInputTokens, result.Usage?.PromptTokens ?? 0);
            Interlocked.Add(ref _totalOutputTokens, result.Usage?.CompletionTokens ?? 0);
            if (LlmClient.CacheStatsSupported)
            {
                Interlocked.Add(ref _totalCacheHitTokens, result.Usage?.CacheHitTokens ?? 0);
                Interlocked.Add(ref _totalCacheMissTokens, result.Usage?.CacheMissTokens ?? 0);
            }

            // ---- LLM 调用轨迹（Debug）----
            // 过滤交由 BepInEx 的 listener 按 BepInEx.cfg 的 LogLevels 处理；
// 此处无条件构造并发出，避免本插件与框架重复维护一份日志级别开关。
            // 只记录本轮新输入：首轮(_traceTurnCount==0)展开系统提示词(系统:...) + 用户(inputJson)；
            // 后续轮系统提示词与首轮一致（术语表若合并增长才变化），仅记录用户新输入。
            // 思考(reasoning_content)置于输入与输出之间，输出为完整 JSON，均不截断。
            // 尾行整合耗时/token/缓存/累计；接口未返回 token 时回退字符估算(0.75)。
            {
                long inTok = result.Usage?.PromptTokens ?? 0;
                long outTok = result.Usage?.CompletionTokens ?? 0;
                bool estimated = (inTok == 0 && outTok == 0);
                if (estimated)
                {
                    inTok = ctxTokens;
                    outTok = result.FullResponse.Length * 3 / 4;
                }

                var trace = new StringBuilder();
                trace.Append("[LLM调用] 批次").Append(batchId)
                     .Append(" 选取").Append(batch.Count).Append("条(").Append(totalChars)
                     .Append("字符) 上下文").Append(ctxTokens).Append("/").Append(_config.MaxContext)
                     .Append(" 排队").Append(waitMs).Append("ms 第").Append(turnCount + 1).Append("次(无历史)")
                     .Append("\n  实际输入: ");
                if (firstTurn)
                    trace.Append("系统:").Append(Flatten(systemPrompt))
                         .Append(" | 用户:").Append(Flatten(inputJson));
                else
                    trace.Append("用户:").Append(Flatten(inputJson));
                // 思考过程（reasoning_content）：先于输出的内容流式到达，置于输入与输出之间，
                // 便于和输入/输出对照核实模型锚定/续写之类的行为。无思考输出时省略此行。
                if (!string.IsNullOrEmpty(result.Reasoning))
                    trace.Append("\n  思考: ").Append(Flatten(result.Reasoning));
                trace.Append("\n  输出: ").Append(Flatten(result.FullResponse))
                     .Append("\n  耗时").Append(result.ElapsedMs).Append("ms ");
                if (estimated)
                    trace.Append("输入~").Append(inTok).Append("tokens(估算) 输出~")
                         .Append(outTok).Append("tokens(估算)");
                else
                {
                    trace.Append("输入").Append(inTok).Append("tokens 输出")
                         .Append(outTok).Append("tokens");
                    if (LlmClient.CacheStatsSupported)
                    {
                        long hit = result.Usage?.CacheHitTokens ?? 0;
                        long miss = result.Usage?.CacheMissTokens ?? 0;
                        trace.Append(" [缓存命中").Append(hit).Append(" 未中").Append(miss).Append("]");
                    }
                }
                trace.Append(" 累计:入").Append(_totalInputTokens)
                     .Append(" 出").Append(_totalOutputTokens);
                if (LlmClient.CacheStatsSupported)
                    trace.Append(" 命中").Append(_totalCacheHitTokens)
                         .Append(" 未中").Append(_totalCacheMissTokens);
                Logger.Debug(trace.ToString());
            }

            if (string.IsNullOrEmpty(result.FullResponse))
                throw new Exception("翻译结果为空");

            // 解析响应并分发译文到批内任务（半角化封装在内部）
            // 双线程架构下翻译输出不再带 glossary；解析器仍兼容旧式输出，此处忽略其 glossary 出参
            Dictionary<string, object>? glossaryObj;
            int completed = BatchResponseParser.ParseAndDispatch(result, batch, _config, out glossaryObj);

            // 术语抽取改由独立的术语抽取线程负责：翻译线程只把本批原文投递给它
            if (_config.AutoGlossary && _glossaryWorker != null)
            {
                _glossaryWorker.EnqueueSources(texts);
            }

            if (completed == batch.Count)
            {
                // 对话历史已禁用：不再记录 API token 与对话交换。
                // 仅维护本地调用计数，用于调用轨迹区分"首轮/后续"（系统提示词是否值得展开记录）。
                _traceTurnCount++;
            }
            else if (completed < batch.Count)
            {
                Logger.Warn("批次 " + batchId + ": 解析不完整 | 期望" + batch.Count + "条 实际" + completed + "条");
            }
        }
        catch (WebException we)
        {
            int statusCode = 0;
            if (we.Response is HttpWebResponse httpResp)
            {
                statusCode = (int)httpResp.StatusCode;
                // 读取错误响应体（日志用）
                try
                {
                    using (var errorStream = httpResp.GetResponseStream())
                    using (var reader = new StreamReader(errorStream))
                    {
                        string errorText = reader.ReadToEnd();
                        Logger.Error("服务器错误响应: " + Truncate(errorText, 200));
                    }
                }
                catch { }
            }

            if (statusCode == 429)
            {
                isRateLimit = true;
                _rateLimitGuard.OnRateLimited();
                Logger.Info("限速退避: " + (_rateLimitGuard.CurrentDelayMs / 1000) + " 秒");
            }
            else
            {
                _rateLimitGuard.Reset();
            }
            Logger.Error("翻译失败 [HTTP " + statusCode + "]", we);
        }
        catch (Exception ex)
        {
            Logger.Error("翻译失败", ex);
            _rateLimitGuard.Reset();
        }
        finally
        {
            Interlocked.Decrement(ref _processingCount);

            if (isRateLimit)
            {
                // 限速重试：重新入队，不消耗重试次数
                bool any = false;
                foreach (var task in batch)
                {
                    if (task.State != TaskState.Completed && task.State != TaskState.Failed)
                    {
                        _taskQueue.ReEnqueue(task);
                        any = true;
                    }
                }
                if (any)
                    Logger.Warn("批次 " + batchId + ": 限速重试 " + batch.Count + " 条（不消耗重试次数）");
            }
            else
            {
                // 正常错误重试
                int retried = 0, failed = 0;
                foreach (var task in batch)
                {
                    if (task.State == TaskState.Completed || task.State == TaskState.Failed)
                        continue;

                    if (_retryHandler.ShouldRetry(task))
                    {
                        _retryHandler.IncrementRetry(task);
                        _taskQueue.ReEnqueue(task);
                        retried++;
                    }
                    else
                    {
                        Logger.Error("重试耗尽(共" + _config.MaxRetry + "次) | 放弃: " + Truncate(task.UntranslatedText, 80));
                        task.MarkFailed("翻译失败，已重试" + _config.MaxRetry + "次");
                        _taskQueue.MarkCompleted();
                        failed++;
                    }
                }
                if (retried > 0 || failed > 0)
                    Logger.Info("批次 " + batchId + ": " + retried + " 条重试 " + failed + " 条放弃");
            }

            // 标记成功完成的任务
            foreach (var task in batch)
            {
                if (task.State == TaskState.Completed)
                    _taskQueue.MarkCompleted();
            }
        }
    }

    // ---- 辅助 ----

    // 历史清空驱动的术语表维护（OnHistoryCleared）已移除：对话历史已禁用，不再有清空事件。
    // 术语 _pendingNew → _glossary 的晋升改由术语抽取线程按 GlossaryMergeThreshold 阈值单点驱动，
    // 译文系统提示词每批从 _glossary 即时重建，新术语延迟一批即生效。
    // _history 的系统提示词 token 基线不再随术语表增长刷新（禁用后仅作保守估算，偏低无副作用）。

    /// <summary>截断文本用于日志输出，避免打印超长内容。</summary>
    /// <summary>
    /// 将字符串中的换行符转义为字面量 \n，使日志输出保持单行；
    /// 调用轨迹里输入/输出可能内含换行（如美化后的 JSON），统一扁平化以便检索。
    /// </summary>
    private static string Flatten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text!.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxLen) return text;
        return text.Substring(0, maxLen) + "...";
    }

    /// <summary>
    /// 构建用户输入 JSON。
    /// 键取自各 task.UserKey（由 ConversationHistory.AllocKeys 分配的 "1"/"2"/...，
    /// 同一对话历史窗口内全局单调递增、绝不重号），值为该任务原文：
    ///   {"1":"原文1","2":"原文2",...}
    /// 输出端 BatchResponseParser 用同样的键读取译文，键值对应、与历史中旧批重号条目
    /// 不再混淆。未完成系统提示词自定义工作，此处不内置示例/规则文本。
    /// </summary>
    private static string BuildInputJson(List<TranslationTask> batch)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        for (int i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(SimpleJson.Serialize(batch[i].UserKey))
              .Append(':')
              .Append(SimpleJson.Serialize(batch[i].UntranslatedText));
        }
        sb.Append('}');
        return sb.ToString();
    }
}
