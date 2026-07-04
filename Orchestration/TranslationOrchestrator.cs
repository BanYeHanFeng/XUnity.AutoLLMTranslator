using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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

    private volatile bool _shutdownRequested;
    private volatile int _processingCount;
    private int _batchSeq;
    private Thread? _workerThread;
    private long _totalInputTokens, _totalOutputTokens;
    private long _totalCacheHitTokens, _totalCacheMissTokens;

    // HalfWidthRegex: 全角符号 [！-～] (U+FF01 - U+FF5E)
    // 用 Unicode 范围替代显式字符列表，避免 verbatim string 中 "" 转义带来的字符类构成错误
    // （旧实现误包含半角 " U+0022 而漏掉全角 ＂ U+FF02，导致半角双引号被错误映射到 U+0142）
    private static readonly Regex HalfWidthRegex =
        new Regex(@"[\uFF01-\uFF5E]", RegexOptions.Compiled);

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
            Enabled = config.ParallelCount <= 1,
            MaxContext = config.MaxContext
        };

        // 术语表管理器（AutoGlossary=true 时启用）
        if (config.AutoGlossary)
        {
            _glossary = new GlossaryManager(config.GlossaryPath);
            // 术语表模式使用合并术语表后的系统提示词作为缓存基线
            var fullPrompt = _glossary.BuildSystemPrompt(config.CachedGlossaryPrompt);
            _history.InitSystemPrompt(fullPrompt);
        }
        else
        {
            _history.InitSystemPrompt(config.CachedSystemPrompt);
        }
    }

    // ---- 公开方法 ----

    /// <summary>启动后台工作线程。</summary>
    public void Start()
    {
        _workerThread = new Thread(WorkerLoop) { IsBackground = true };
        _workerThread.Start();
    }

    /// <summary>请求关闭。</summary>
    public void Shutdown()
    {
        _shutdownRequested = true;
        try { _taskQueue.Signal.Set(); } catch { }
    }

    // ---- 工作线程主循环 ----

    private void WorkerLoop()
    {
        while (!_shutdownRequested)
        {
            // 等待新任务或 50ms 超时（保底轮询）
            _taskQueue.Signal.WaitOne(50);

            // 限速检查
            if (_rateLimitGuard.IsBlocked())
                continue;

            // 并发控制
            if (_processingCount >= _config.ParallelCount)
                continue;

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
        while (_processingCount < _config.ParallelCount)
        {
            // 1. 取出所有兼容任务（无字符上限）
            var pending = _taskQueue.DequeueAll();
            if (pending.Count == 0)
                break;

            // 2. 历史超限检查（token 精准判断）
            if (_history.CheckAndClearIfOverLimit())
            {
                // 历史清空 → 合并本轮新术语到文件并更新系统提示词
                OnHistoryCleared();
            }

            // 3. Token 感知任务选择（复杂超限处理封装在 SelectBatch 内）
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
    /// 超限任务的处理策略：
    ///   - batch 非空时超限 → 本条及后续全归 overflow（"下一句"截断逻辑）
    ///   - batch 为空时单条自身超限 → MarkFailed 丢弃
    ///   - batch 为空时历史太满 → ClearHistory 后纳入本条
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
                task.MarkFailed("单条文本超出 MaxContext(" + _config.MaxContext + ")");
                _taskQueue.MarkCompleted();
                _history.IncrementDiscardCount();
                Logger.Warn("单条文本超出最大上下文(" + _config.MaxContext + "), 丢弃(第" +
                    _history.DiscardCount + "次) | 估算" + taskEstimate +
                    " tokens | 文本: " + Truncate(task.UntranslatedText, 80));
                estimatedTotal = _history.TotalContextTokens;   // 重置
                continue;
            }

            // 历史太满导致装不下 → 清空历史后纳入本条
            Logger.Info("上下文接近上限(" + estimatedTotal + "/" + _config.MaxContext + "), " +
                "清空历史以容纳新任务 | 文本估算" + taskEstimate + " tokens");
            _history.ClearHistory();
            OnHistoryCleared();
            estimatedTotal = _history.TotalContextTokens;
            batch.Add(task);
            estimatedTotal += taskEstimate;
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

            // 构建用户输入 JSON
            string inputJson = BuildInputJson(texts);

            // 构建消息：术语表模式使用含术语表的系统提示词，否则用默认
            string systemPrompt = _config.AutoGlossary && _glossary != null
                ? _glossary.BuildSystemPrompt(_config.CachedGlossaryPrompt)
                : _config.CachedSystemPrompt;
            var messages = _history.BuildMessages(systemPrompt, inputJson);

            // ---- 批次开始（Debug） ----
            if (Logger.IsDebugEnabled)
            {
                int totalChars = 0;
                foreach (var t in texts) totalChars += t.Length;
                long waitMs = Environment.TickCount - batch[0].CreatedTick;
                int ctxTokens = _history.TotalContextTokens;
                Logger.Debug(
                    "批次 " + batchId + ": 选取" + batch.Count + "条(" + totalChars + "字符) | " +
                    "上下文" + ctxTokens + "/" + _config.MaxContext + " | " +
                    "排队" + waitMs + "毫秒 历史" + _history.TurnCount + "轮 并行" +
                    _processingCount + "/" + _config.ParallelCount);
            }

            // 同步调用 LLM（阻塞 ThreadPool 线程，net35 下无可避免）
            LlmResult result = _llmClient.Translate(
                _config.Url, _config.ApiKey, _config.Model,
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

            // ---- 批次完成（Debug） ----
            if (Logger.IsDebugEnabled)
            {
                long inputTok = result.Usage?.PromptTokens ?? 0;
                long outputTok = result.Usage?.CompletionTokens ?? 0;
                if (LlmClient.CacheStatsSupported)
                {
                    long hit = result.Usage?.CacheHitTokens ?? 0;
                    long miss = result.Usage?.CacheMissTokens ?? 0;
                    Logger.Debug(
                        "批次 " + batchId + ": 完成 耗时" + result.ElapsedMs + "毫秒 | " +
                        "输入" + inputTok + "tokens 输出" + outputTok + "tokens " +
                        "[缓存命中" + hit + " 未中" + miss + "] | " +
                        "累计: 入" + _totalInputTokens + " 出" + _totalOutputTokens +
                        " 命中" + _totalCacheHitTokens + " 未中" + _totalCacheMissTokens);
                }
                else
                {
                    long speed = outputTok > 0 && result.ElapsedMs > 0
                        ? outputTok * 1000 / result.ElapsedMs : 0;
                    Logger.Debug(
                        "批次 " + batchId + ": 完成 耗时" + result.ElapsedMs + "毫秒 | " +
                        "输入" + inputTok + "tokens 输出" + outputTok + "tokens " +
                        speed + "tokens/s | " +
                        "累计: 入" + _totalInputTokens + " 出" + _totalOutputTokens);
                }
            }

            if (string.IsNullOrEmpty(result.FullResponse))
                throw new Exception("翻译结果为空");

            // 解析响应
            var resultObj = SimpleJson.ParseJsonObject(result.FullResponse);
            if (resultObj == null || resultObj.Count == 0)
                throw new Exception("JSON结果解析失败: " + result.FullResponse);

            // 分发结果：术语表模式用 translations 嵌套结构，否则用扁平数字 key
            Dictionary<string, object>? translationsObj = null;
            Dictionary<string, object>? glossaryObj = null;
            if (_config.AutoGlossary && resultObj.TryGetValue("translations", out object? tObj)
                && tObj is Dictionary<string, object> tDict)
            {
                translationsObj = tDict;
                if (resultObj.TryGetValue("glossary", out object? gObj)
                    && gObj is Dictionary<string, object> gDict)
                    glossaryObj = gDict;
            }
            else
            {
                // 非术语表模式：整个对象就是译文映射
                translationsObj = resultObj;
            }

            int completed = 0;
            foreach (var kvp in translationsObj)
            {
                int index;
                if (!int.TryParse(kvp.Key, out index)) continue;
                if (index < 1 || index > batch.Count) continue;

                string translated = (kvp.Value as string) ?? "";
                if (string.IsNullOrEmpty(translated)) continue;

                // 全角转半角
                if (_config.HalfWidth)
                    translated = HalfWidthRegex.Replace(translated,
                        m => ((char)(m.Value[0] - 0xFEE0)).ToString());

                batch[index - 1].MarkCompleted(translated);
                completed++;
            }

            // 术语表模式：收集本轮新术语（暂存内存，历史清空时落盘）
            if (_config.AutoGlossary && _glossary != null && glossaryObj != null)
            {
                _glossary.AddPendingTerms(glossaryObj);
            }

            if (completed == batch.Count)
            {
                // 1. 先记录 API 精确 token（如果可用）
                _history.RecordApiUsage(
                    result.Usage?.PromptTokens ?? 0,
                    result.Usage?.CompletionTokens ?? 0);
                // 2. 再记录对话交换（精确模式下只追加消息，回退模式下累加估算）
                _history.RecordExchange(inputJson, result.FullResponse);
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

    /// <summary>
    /// 历史清空后的术语表维护：
    /// 1. 将本轮收集的新术语合并到文件
    /// 2. 用合并后的术语表重建系统提示词并更新 token 估算
    /// 仅在 AutoGlossary=true 时有实际效果。
    /// </summary>
    private void OnHistoryCleared()
    {
        if (!_config.AutoGlossary || _glossary == null) return;
        int added = _glossary.MergePendingAndSave();
        if (added > 0)
        {
            // 术语表变化 → 系统提示词变长 → 更新基线 token 估算
            var fullPrompt = _glossary.BuildSystemPrompt(_config.CachedGlossaryPrompt);
            _history.UpdateSystemPrompt(fullPrompt);
        }
    }

    /// <summary>截断文本用于日志输出，避免打印超长内容。</summary>
    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxLen) return text;
        return text.Substring(0, maxLen) + "...";
    }

    /// <summary>
    /// 构建 {"1":"原文1","2":"原文2",...} 格式的用户输入 JSON。
    /// 与原始 BuildInputJson 逻辑完全一致。
    /// </summary>
    private static string BuildInputJson(List<string> texts)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        for (int i = 0; i < texts.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(i + 1).Append("\":");
            sb.Append(SimpleJson.Serialize(texts[i]));
        }
        sb.Append('}');
        return sb.ToString();
    }
}
