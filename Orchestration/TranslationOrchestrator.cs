#nullable disable
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

    private volatile bool _shutdownRequested;
    private volatile int _processingCount;
    private int _batchSeq;
    private Thread _workerThread;
    private long _totalInputTokens, _totalOutputTokens;
    private long _totalCacheHitTokens, _totalCacheMissTokens;

    // HalfWidthRegex: [！-～] (0xFF01-0xFF5E)
    private static readonly Regex HalfWidthRegex =
        new Regex(@"[！""＃＄％＆＇（）＊＋，－．／０１２３４５６７８９：；＜＝＞？＠［＼］＾＿｀｛｜｝～]",
                  RegexOptions.Compiled);

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
            var batch = _taskQueue.DequeueBatch(_config.MaxWordCount);
            if (batch.Count == 0)
                break;

            // 标记为 Processing
            foreach (var task in batch)
                task.State = TaskState.Processing;
            _processingCount++;

            var capturedBatch = batch;
            ThreadPool.QueueUserWorkItem(_ => ProcessBatch(capturedBatch));
        }
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

            // 检查对话历史
            _history.CheckAndClearIfOverLimit(_config.CachedSystemPrompt, inputJson);

            // 构建消息
            var messages = _history.BuildMessages(_config.CachedSystemPrompt, inputJson);

            // 日志
            int totalChars = 0;
            foreach (var t in texts) totalChars += t.Length;
            long waitMs = Environment.TickCount - batch[0].CreatedTick;
            Logger.Info("批次 " + batchId + ": 发送 " + texts.Count + " 条, " + totalChars + " 字符, " +
                "排队" + waitMs + "ms, 历史" + _history.TurnCount + "轮, " +
                "并行" + _processingCount + "/" + _config.ParallelCount);

            // 同步调用 LLM（阻塞 ThreadPool 线程，net35 下无可避免）
            LlmResult result = _llmClient.Translate(
                _config.Url, _config.ApiKey, _config.Model,
                messages, _config.ParsedModelParams);

            _rateLimitGuard.Reset();

            // Token 统计
            _totalInputTokens += result.Usage?.PromptTokens ?? 0;
            _totalOutputTokens += result.Usage?.CompletionTokens ?? 0;
            if (LlmClient.CacheStatsSupported)
            {
                _totalCacheHitTokens += result.Usage?.CacheHitTokens ?? 0;
                _totalCacheMissTokens += result.Usage?.CacheMissTokens ?? 0;
                Logger.Info("LLM usage: 入" + result.Usage.PromptTokens + " 出" + result.Usage.CompletionTokens + " " +
                    "命中" + result.Usage.CacheHitTokens + " 未中" + result.Usage.CacheMissTokens + " | " +
                    "累计: 入" + _totalInputTokens + " 出" + _totalOutputTokens + " 命中" + _totalCacheHitTokens + " 未中" + _totalCacheMissTokens);
            }
            else
            {
                Logger.Info("LLM usage: 入" + (result.Usage?.PromptTokens ?? 0) + " 出" + (result.Usage?.CompletionTokens ?? 0) + " | " +
                    "累计: 入" + _totalInputTokens + " 出" + _totalOutputTokens);
            }

            if (result.ElapsedMs > 0 && (result.Usage?.CompletionTokens ?? 0) > 0)
                Logger.Info("LLM 速度: " + (result.Usage.CompletionTokens * 1000 / result.ElapsedMs) + " tok/s, 耗时" + result.ElapsedMs + "ms");

            if (string.IsNullOrEmpty(result.FullResponse))
                throw new Exception("翻译结果为空");

            // 解析响应
            var resultObj = SimpleJson.ParseJsonObject(result.FullResponse);
            if (resultObj == null || resultObj.Count == 0)
                throw new Exception("JSON结果解析失败: " + result.FullResponse);

            // 分发结果
            int completed = 0;
            foreach (var kvp in resultObj)
            {
                int index;
                if (!int.TryParse(kvp.Key, out index)) continue;
                if (index < 1 || index > batch.Count) continue;

                string translated = kvp.Value as string;
                if (string.IsNullOrEmpty(translated)) continue;

                // 全角转半角
                if (_config.HalfWidth)
                    translated = HalfWidthRegex.Replace(translated,
                        m => ((char)(m.Value[0] - 0xFEE0)).ToString());

                batch[index - 1].MarkCompleted(translated);
                completed++;
            }

            if (completed == batch.Count)
            {
                _history.AppendExchange(inputJson, result.FullResponse);
            }
            else if (completed < batch.Count)
            {
                Logger.Warn("批次 " + batchId + ": 解析不完整, 期望" + batch.Count + "条 实际" + completed + "条");
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
                        Logger.Error("服务器错误响应: " + errorText);
                    }
                }
                catch { }
            }

            if (statusCode == 429)
            {
                isRateLimit = true;
                _rateLimitGuard.OnRateLimited();
                Logger.Warn("限速退避: " + (_rateLimitGuard.CurrentDelayMs / 1000) + "s");
            }
            else
            {
                _rateLimitGuard.Reset();
            }
            Logger.Error("翻译失败 [" + statusCode + "]: " + we.Message);
        }
        catch (Exception ex)
        {
            Logger.Error("翻译失败: " + ex.Message);
            _rateLimitGuard.Reset();
        }
        finally
        {
            _processingCount--;

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
                    Logger.Info("批次 " + batchId + ": 限速重试 " + batch.Count + " 条（不消耗重试次数）");
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
                        Logger.Error("重试耗尽(" + _config.MaxRetry + "次), 放弃: " + task.UntranslatedText);
                        task.MarkFailed("翻译失败，已重试" + _config.MaxRetry + "次");
                        _taskQueue.MarkCompleted();
                        failed++;
                    }
                }
                if (retried > 0 || failed > 0)
                    Logger.Info("批次 " + batchId + ": " + retried + " 条重试, " + failed + " 条放弃");
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
