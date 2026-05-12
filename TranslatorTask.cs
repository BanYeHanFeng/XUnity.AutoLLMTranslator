using System.Net;
using System.Text.RegularExpressions;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;

public class TranslatorTask
{
    public class TaskData
    {
        public enum TaskState
        {
            Waiting,
            Processing,
            Completed,
            Failed,
            Closed,
        }
        public HttpListenerContext? context { get; set; }
        public string[] texts { get; set; }
        public string[] result { get; set; }
        public int retryCount { get; set; }
        public TaskState state = TaskState.Waiting;
        public long addTick;
        public int charLen;

        //响应
        public bool TryRespond()
        {
            try
            {
                if (context == null || context.Response == null) return false;
                if (state == TaskState.Completed || state == TaskState.Failed)
                {
                    // 返回响应
                    string responseString = SimpleJson.SerializeTexts(result);
                    byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    context.Response.Close();
                    state = TaskState.Closed;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"响应失败: {ex.Message}");
            }            
            return false;
        }
    }


    private static readonly Regex HalfWidthRegex = new Regex(@"[！""＃＄％＆＇（）＊＋，－．／０１２３４５６７８９：；＜＝＞？＠［＼］＾＿｀｛｜｝～]", RegexOptions.Compiled);

    private const int MaxQueueSize = 2000;

    private string? _apiKey;
    private string? _model;
    private string? _url;
    private int _maxWordCount = 2500;
    private int _parallelCount = 1;
    private int _maxRetry = 10;
    private int _maxContext = 1024;
    private string _modelParamsRaw = "";
    private Dictionary<string, object> _parsedModelParams = new Dictionary<string, object>();
    private string _cachedSystemPromptBase = "";
    private string? _extraPrompt;
    private bool _halfWidth = true;
    private string? DestinationLanguage;
    private string? SourceLanguage;

    // 用 Queue 替代 List：SelectTasks O(1) 出队，避免全表扫描
    Queue<TaskData> _waitingQueue = new Queue<TaskData>();
    HttpListener listener;
    bool _initialized = false;
    private int _port = 20000;
    public int Port => _port;
    ConversationHistory _history = new ConversationHistory();
    long _totalInputTokens = 0;
    long _totalOutputTokens = 0;
    long _totalCacheHitTokens = 0;
    long _totalCacheMissTokens = 0;
    private volatile int _rateLimitDelayMs = 0;
    private volatile bool _shutdownRequested = false;
    private volatile int curProcessingCount = 0;
    // 追踪未响应任务总量（用于积压告警）
    private volatile int _totalOutstandingTasks = 0;
    private AutoResetEvent _taskAvailable = new AutoResetEvent(false);
    int _batchSeq = 0;
    private readonly object _lockObject = new object();

    public void Init(IInitializationContext context)
    {
        _model = context.GetOrCreateSetting("AutoLLM", "Model", "");
        _url = context.GetOrCreateSetting("AutoLLM", "URL", "");
        _apiKey = context.GetOrCreateSetting("AutoLLM", "APIKey", "");
        _maxWordCount = context.GetOrCreateSetting("AutoLLM", "MaxWordCount", 2500);
        _parallelCount = context.GetOrCreateSetting("AutoLLM", "ParallelCount", 1);
        _maxRetry = context.GetOrCreateSetting("AutoLLM", "MaxRetry", 10);
        _maxContext = context.GetOrCreateSetting("AutoLLM", "MaxContext", 1024);
        _modelParamsRaw = context.GetOrCreateSetting("AutoLLM", "ModelParams", "");
        _extraPrompt = context.GetOrCreateSetting("AutoLLM", "ExtraPrompt", "");
        _halfWidth = context.GetOrCreateSetting("AutoLLM", "HalfWidth", true);

        // P1: ModelParams 预先解析一次，避免每批次重复 JSON 解析
        if (!string.IsNullOrEmpty(_modelParamsRaw))
            _parsedModelParams = SimpleJson.ParseModelParams(_modelParamsRaw);

        // 定位 BepInEx 根目录：从 TranslatorDirectory 向上找含 core/ 子目录或目录名为 BepInEx 的目录
        var dir = context.TranslatorDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "core"))) break;
            if ((Path.GetFileName(dir) ?? "").Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) break;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir || string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }
        Logger.Init(dir);
        if (context.GetOrCreateSetting("AutoLLM", "DisableSpamChecks", true))
        {
            context.DisableSpamChecks();
        }

        ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, _parallelCount * 2);
        ServicePointManager.Expect100Continue = false;

        _history.Enabled = _parallelCount <= 1;
        _history.MaxContext = _maxContext;

        if (_url.EndsWith("/v1"))
        {
            _url += "/chat/completions";
        }
        if (_url.EndsWith("/v1/"))
        {
            _url += "chat/completions";
        }

        DestinationLanguage = context.DestinationLanguage;
        SourceLanguage = context.SourceLanguage;
        if (string.IsNullOrEmpty(_model) || string.IsNullOrEmpty(_url))
        {
            Logger.Error("Model 或 URL 未配置，翻译功能已禁用");
            return;
        }
        if (string.IsNullOrEmpty(_apiKey))
        {
            Logger.Info("APIKey 未配置，Authorization 头将不发送");
        }

        // P2: 预编译 system prompt 的固定部分（语言不会变），减少每批次的字符串替换
        _cachedSystemPromptBase = Config.prompt_base
            .Replace("{{TARGET_LAN}}", DestinationLanguage)
            .Replace("{{SOURCE_LAN}}", SourceLanguage);

        // P8: 确保 ThreadPool 有足够线程处理并发翻译
        int minWorker, minIo;
        ThreadPool.GetMinThreads(out minWorker, out minIo);
        int needed = _parallelCount + 2;
        if (minWorker < needed || minIo < needed)
            ThreadPool.SetMinThreads(Math.Max(minWorker, needed), Math.Max(minIo, needed));

        const int MaxPortAttempts = 10;
        for (int attempt = 0; attempt < MaxPortAttempts; attempt++)
        {
            try
            {
                _port = 20000 + attempt;
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                listener.Start();
                break;
            }
            catch (Exception ex)
            {
                if (attempt == MaxPortAttempts - 1)
                {
                    Logger.Error($"无法绑定任何端口 (20000-{20000 + MaxPortAttempts - 1}): {ex.Message}");
                    return;
                }
                Logger.Warn($"端口 {_port} 被占用，尝试 {_port + 1}");
            }
        }
        Logger.Info($"已启动 | Model={_model} URL={_url} MaxWordCount={_maxWordCount} MaxContext={_maxContext} ParallelCount={_parallelCount} MaxRetry={_maxRetry} HalfWidth={_halfWidth} ExtraPrompt={(string.IsNullOrEmpty(_extraPrompt) ? "无" : (_extraPrompt.Length + "字"))} ModelParams={_modelParamsRaw} DisableSpamChecks=True");
        Logger.Info($"Listening for requests on http://127.0.0.1:{_port}/");


        // Start a separate thread for HTTP listener
        Thread listenerThread = new Thread(() =>
        {
            try
            {
                while (!_shutdownRequested)
                {
                    var ctx = listener.GetContext();
                    ProcessRequest(ctx);
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Logger.Error($"HTTP listener error: {ex.Message}");
            }
        });
        listenerThread.IsBackground = true;
        listenerThread.Start();

        Thread pollingThread = new Thread(Polling);
        pollingThread.IsBackground = true;
        pollingThread.Start();
        _initialized = true;
    }

    private void ProcessRequest(HttpListenerContext context)
    {
        try
        {
            if (!_initialized)
            {
                context.Response.Close();
                return;
            }
            if (Logger.IsDebugEnabled) Logger.Debug($"处理请求: {context.Request.HttpMethod} {context.Request.Url}");
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            if (request.HttpMethod == "POST")
            {
                // 读取请求体
                using (Stream body = request.InputStream)
                using (StreamReader reader = new StreamReader(body, request.ContentEncoding))
                {
                    string requestBody = reader.ReadToEnd();
                    var texts = SimpleJson.ParseTexts(requestBody);
                    if (texts.Length > 0)
                    {
                        AddTask(texts, context);
                    }
                    else
                    {
                        context.Response.Close();
                    }
                }
            }
            else if (request.HttpMethod == "GET")
            {
                string responseString = "AutoLLMTranslator is running.";
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
            else
            {
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"处理请求时发生错误: {ex.Message}");
            context.Response.Close();
        }
    }

    /// <summary>
    /// 从等待队列中选取一批任务。调用者必须已持有 _lockObject。
    /// 优先规则：不混搭重试和非重试任务，retryCount>2 的单独成批。
    /// </summary>
    List<TaskData> SelectTasks()
    {
        var tasks = new List<TaskData>();
        int toltoken = 0;
        // caller holds _lockObject — 不再内部获取锁
        int count = _waitingQueue.Count;
        while (count > 0)
        {
            var task = _waitingQueue.Peek();

            // 优先规则
            if (tasks.Count > 0)
            {
                // 不混搭重试和非重试
                if ((tasks[0].retryCount > 0) != (task.retryCount > 0))
                    break;
                // 已有任务时，跳过 retryCount > 2 的高重试任务（另起一批）
                if (task.retryCount > 2)
                    break;
            }

            // 超出字数上限时截断（至少保证有1条）
            if (toltoken + task.charLen > _maxWordCount && tasks.Count > 0)
                break;

            _waitingQueue.Dequeue();
            tasks.Add(task);
            toltoken += task.charLen;
            count--;
        }
        return tasks;
    }

    public TaskData AddTask(string[] texts, HttpListenerContext context)
    {
        if (texts == null || texts.Length == 0)
        {
            Logger.Debug("添加任务: 空文本，跳过");
            return null;
        }
        if (Logger.IsDebugEnabled) Logger.Debug($"添加任务: {string.Join(", ", texts)}");
        int totalLen = 0;
        foreach (var t in texts) totalLen += t.Length;
        var task = new TaskData() { texts = texts, context = context, charLen = totalLen };
        task.addTick = Environment.TickCount;

        lock (_lockObject)
        {
            // P9: 任务积压上限保护
            if (_totalOutstandingTasks >= MaxQueueSize)
            {
                Logger.Warn($"任务队列已达上限({MaxQueueSize})，拒绝新任务");
                try { context.Response.Close(); } catch { }
                return null;
            }

            _waitingQueue.Enqueue(task);
            _totalOutstandingTasks++;
        }

        // P6: 唤醒轮询线程立即处理
        _taskAvailable.Set();

        return task;
    }

    private string BuildInputJson(List<string> texts)
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

    void TaskRespond(TaskData task)
    {
        if (!task.TryRespond())
            Logger.Warn($"响应发送失败: state={task.state} texts[0]={task.texts?[0]}");
        // P3/P4: 任务已从队列出队，无需再从列表移除。仅递减积压计数。
        Interlocked.Decrement(ref _totalOutstandingTasks);
    }

    void ProcessTaskBatch(List<TaskData> tasks)
    {
        int hashkey = Interlocked.Increment(ref _batchSeq);
        bool isRateLimit = false;
        try
        {
            foreach (var task in tasks)
                if (Logger.IsDebugEnabled) Logger.Debug($"{hashkey} 翻译开始:{task.texts[0]}");

            List<string> texts = new List<string>();
            foreach (var task in tasks)
                texts.AddRange(task.texts);

            // P2: 使用预编译 system prompt，只需替换可能变化的 EXTRA_PROMPT
            var extra = string.IsNullOrEmpty(_extraPrompt) ? "" : "\n\n" + _extraPrompt;
            var system = _cachedSystemPromptBase + extra;

            var inputJson = BuildInputJson(texts);

            _history.CheckAndClearIfOverLimit(system, inputJson);
            var messages = _history.BuildMessages(system, inputJson);

            var totalChars = texts.Sum(t => t.Length);
            Logger.Info($"批次 {hashkey}: 发送 {texts.Count} 条文本, {totalChars} 字符, 历史{_history.TurnCount}轮, 并行 {curProcessingCount}/{_parallelCount}");

            // P1: 传入已解析的 ModelParams Dictionary，避免重复 JSON 解析
            var result = LlmClient.Translate(_url, _apiKey, _model, messages, _parsedModelParams);

            if (Logger.IsDebugEnabled) Logger.Debug($"full流({result.FullResponse.Length}字, {result.ChunkCount}块): {result.FullResponse}");

            _totalInputTokens += result.PromptTokens;
            _totalOutputTokens += result.CompletionTokens;
            _totalCacheHitTokens += result.CacheHitTokens;
            _totalCacheMissTokens += result.CacheMissTokens;

            if (LlmClient.CacheStatsSupported)
                Logger.Info($"LLM usage: 输入{result.PromptTokens} 输出{result.CompletionTokens} 缓存命中{result.CacheHitTokens} 缓存未中{result.CacheMissTokens} | 累计: 入{_totalInputTokens} 出{_totalOutputTokens} 命中{_totalCacheHitTokens} 未中{_totalCacheMissTokens}");
            else
                Logger.Info($"LLM usage: 输入{result.PromptTokens} 输出{result.CompletionTokens} | 累计: 入{_totalInputTokens} 出{_totalOutputTokens}");

            if (result.ElapsedMs > 0 && result.CompletionTokens > 0)
                Logger.Info($"LLM 速度: {result.CompletionTokens * 1000 / result.ElapsedMs} tok/s, 耗时{result.ElapsedMs}ms");

            if (string.IsNullOrEmpty(result.FullResponse))
                throw new Exception("翻译结果为空");

            var resultObj = SimpleJson.ParseJsonObject(result.FullResponse);
            if (resultObj == null || resultObj.Count == 0)
                throw new Exception($"JSON结果解析失败: {result.FullResponse}");

            int i = 0;
            foreach (var kvp in resultObj)
            {
                int num;
                if (!int.TryParse(kvp.Key, out num)) continue;
                if (num < 1 || num > tasks.Count)
                {
                    if (Logger.IsDebugEnabled) Logger.Debug($"{hashkey} 解析结果键越界: 键={kvp.Key} 任务总数={tasks.Count}");
                    continue;
                }

                var rs = kvp.Value as string;
                if (string.IsNullOrEmpty(rs)) continue;

                if (_halfWidth)
                    rs = HalfWidthRegex.Replace(rs, m => ((char)(m.Value[0] - 0xFEE0)).ToString());

                var task = tasks[num - 1];
                task.result = new string[] { rs };
                task.state = TaskData.TaskState.Completed;
                TaskRespond(task);
                if (Logger.IsDebugEnabled) Logger.Debug($"{hashkey} 流OK [{num}]: {rs}");
                i++;
            }
            if (Logger.IsDebugEnabled) Logger.Debug($"{hashkey} 解析完成: {i}/{tasks.Count} 条");
            if (i < tasks.Count)
                Logger.Warn($"{hashkey} 解析结果不完整: 期望{tasks.Count}条 实际{i}条");
            else
                _history.AppendExchange(inputJson, result.FullResponse);
        }
        catch (WebException ex)
        {
            int statusCode = 0;
            if (ex.Response is HttpWebResponse httpResp)
                statusCode = (int)httpResp.StatusCode;
            Logger.Error($"翻译失败 [{statusCode}]: {ex.Message}");
            if (ex.Response != null)
            {
                using (var errorResponse = (HttpWebResponse)ex.Response)
                {
                    using (var reader = new StreamReader(errorResponse.GetResponseStream()))
                    {
                        var errorText = reader.ReadToEnd();
                        Logger.Error($"服务器错误响应: {errorText}");
                    }
                }
            }
            if (statusCode == 429)
            {
                isRateLimit = true;
                _rateLimitDelayMs = _rateLimitDelayMs == 0 ? 5000 : Math.Min(_rateLimitDelayMs * 2, 60000);
                Logger.Warn($"限速退避: {_rateLimitDelayMs / 1000}s");
                Thread.Sleep(_rateLimitDelayMs);
            }
            else
            {
                _rateLimitDelayMs = 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"翻译失败: {ex.Message}");
            _rateLimitDelayMs = 0;
        }
        finally
        {
            if (Logger.IsDebugEnabled) Logger.Debug($"翻译结束:{hashkey} curProcessing={curProcessingCount}");
            if (isRateLimit)
            {
                // 限速重试：重新入队，不消耗重试次数
                bool hasRetry = false;
                lock (_lockObject)
                {
                    foreach (var task in tasks)
                    {
                        if (task.state != TaskData.TaskState.Completed && task.state != TaskData.TaskState.Closed)
                        {
                            task.state = TaskData.TaskState.Waiting;
                            _waitingQueue.Enqueue(task);
                            hasRetry = true;
                        }
                    }
                }
                Logger.Info($"批次 {hashkey}: 限速重试 {tasks.Count} 条（不消耗重试次数）");
                if (hasRetry)
                    _taskAvailable.Set();
            }
            else
            {
                int retried = 0, failed = 0;
                bool hasRetry = false;
                lock (_lockObject)
                {
                    foreach (var task in tasks)
                    {
                        if (task.state == TaskData.TaskState.Completed || task.state == TaskData.TaskState.Closed)
                            continue;
                        task.retryCount++;
                        if (task.retryCount < _maxRetry)
                        {
                            if (Logger.IsDebugEnabled) Logger.Debug($"重试({task.retryCount}/{_maxRetry}): {task.texts[0]}");
                            task.state = TaskData.TaskState.Waiting;
                            _waitingQueue.Enqueue(task);  // 重新入队
                            task.result = null;
                            retried++;
                            hasRetry = true;
                        }
                        else
                        {
                            Logger.Error($"重试耗尽({_maxRetry}次), 放弃: {task.texts[0]}");
                            task.state = TaskData.TaskState.Failed;
                            TaskRespond(task);
                            failed++;
                        }
                    }
                }
                if (retried > 0 || failed > 0) Logger.Info($"批次 {hashkey}: {retried} 条重试, {failed} 条放弃");
                if (hasRetry)
                    _taskAvailable.Set();
            }
            lock (_lockObject)
                curProcessingCount--;
        }
    }

    //轮询
    public void Polling()
    {
        while (!_shutdownRequested)
        {
            try
            {
                // P6: 使用事件等待而非固定 Sleep：有新任务时立即唤醒，否则每 50ms 轮询保底
                _taskAvailable.WaitOne(50);

                if (curProcessingCount >= _parallelCount)
                    continue;

                // P7: 积压统计移到锁内，避免读取不一致
                int waitingToken = 0;
                int queueCount = 0;
                List<List<TaskData>> batches = new List<List<TaskData>>();

                lock (_lockObject)
                {
                    // P3: 扫描等待队列计数
                    foreach (var t in _waitingQueue)
                    {
                        waitingToken += t.charLen;
                        queueCount++;
                    }

                    if (queueCount > 0)
                    {
                        Logger.Info($"触发发送: {queueCount}条, {waitingToken}/{_maxWordCount} 字");
                    }

                    // 批量选取
                    var batch = SelectTasks();
                    while (batch.Count > 0 && curProcessingCount < _parallelCount)
                    {
                        curProcessingCount++;
                        foreach (var task in batch)
                            task.state = TaskData.TaskState.Processing;
                        batches.Add(batch);
                        batch = SelectTasks();
                    }
                }

                // P7: 积压告警移到锁外打印（只读统计数据，精确性要求不高）
                int totalOutstanding = _totalOutstandingTasks;
                if (totalOutstanding > 200)
                    Logger.Warn($"任务积压严重: {totalOutstanding} 条，翻译速度可能跟不上文本到达速度");

                if (batches.Count > 0)
                {
                    foreach (var tasklist in batches)
                    {
                        var totalChars = tasklist.Sum(t => t.charLen);
                        Logger.Info($"批次启动: {tasklist.Count}条 {totalChars}字符 并行 {curProcessingCount}/{_parallelCount}");
                        ThreadPool.QueueUserWorkItem(_ => ProcessTaskBatch(tasklist));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
            }
        }
    }

    public void Shutdown()
    {
        _shutdownRequested = true;
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        try { _taskAvailable.Set(); } catch { }
        try { _taskAvailable.Close(); } catch { }
    }
}
