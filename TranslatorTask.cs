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


    private string? _apiKey;
    private string? _model;
    private string? _url;
    private int _batchTimeoutMs = 1000;
    private int _maxWordCount = 2500;
    private int _historyTurns = -1;
    private int _parallelCount = 1;
    private int _maxRetry = 10;
    private int _maxContext = 0;
    private string _modelParams = "";
    private string? _extraPrompt;
    private bool _halfWidth = true;
    private string? DestinationLanguage;
    private string? SourceLanguage;
    private long _lastAddTime = 0;
    List<TaskData> taskDatas = new List<TaskData>();
    HttpListener listener;
    bool _initialized = false;
    ConversationHistory _history = new ConversationHistory();
    long _totalInputTokens = 0;
    long _totalOutputTokens = 0;
    long _totalCacheHitTokens = 0;
    long _totalCacheMissTokens = 0;
    int _rateLimitDelayMs = 0;

    public void Init(IInitializationContext context)
    {
        _model = context.GetOrCreateSetting("AutoLLM", "Model", "");
        _url = context.GetOrCreateSetting("AutoLLM", "URL", "");
        _apiKey = context.GetOrCreateSetting("AutoLLM", "APIKey", "");
        _batchTimeoutMs = context.GetOrCreateSetting("AutoLLM", "BatchTimeout", 1000);
        _maxWordCount = context.GetOrCreateSetting("AutoLLM", "MaxWordCount", 2500);
        _historyTurns = context.GetOrCreateSetting("AutoLLM", "History", -1);
        _parallelCount = context.GetOrCreateSetting("AutoLLM", "ParallelCount", 1);
        _maxRetry = context.GetOrCreateSetting("AutoLLM", "MaxRetry", 10);
        _maxContext = context.GetOrCreateSetting("AutoLLM", "MaxContext", 0);
        _modelParams = context.GetOrCreateSetting("AutoLLM", "ModelParams", "");
        _extraPrompt = context.GetOrCreateSetting("AutoLLM", "ExtraPrompt", "");
        _halfWidth = context.GetOrCreateSetting("AutoLLM", "HalfWidth", true);
        if (context.GetOrCreateSetting("AutoLLM", "DisableSpamChecks", true))
        {
            context.DisableSpamChecks();
        }

        ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, _parallelCount * 2);
        ServicePointManager.Expect100Continue = false;

        _history.Enabled = _historyTurns != 0 && _parallelCount <= 1;
        _history.MaxTurns = _historyTurns;
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
        if (string.IsNullOrEmpty(_apiKey) && !_url.Contains("localhost") && !_url.Contains("127.0.0.1") && !_url.Contains("192.168."))
        {
            throw new Exception("The AutoLLM endpoint requires an API key which has not been provided.");
        }

        listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:20000/");
        listener.Start();
        Logger.Info($"已启动 | Model={_model} URL={_url} History={_historyTurns} MaxWordCount={_maxWordCount} MaxContext={_maxContext} ParallelCount={_parallelCount} BatchTimeout={_batchTimeoutMs}ms MaxRetry={_maxRetry} HalfWidth={_halfWidth} ExtraPrompt={(string.IsNullOrEmpty(_extraPrompt) ? "无" : (_extraPrompt.Length + "字"))} ModelParams={_modelParams} DisableSpamChecks=True");
        Logger.Info("Listening for requests on http://127.0.0.1:20000/");


        // Start a separate thread for HTTP listener
        Thread listenerThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var ctx = listener.GetContext();
                    ProcessRequest(ctx);
                }
            }
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
        Logger.Debug("轮询线程已启动");
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
            Logger.Debug($"处理请求: {context.Request.HttpMethod} {context.Request.Url}");
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
                        var task = AddTask(texts, context);
                    }
                }
            }
            if (request.HttpMethod == "GET")
            {
                // 处理 GET 请求
                string responseString = "AutoLLMTranslator is running.";
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"处理请求时发生错误: {ex.Message}");
            context.Response.Close();
        }
        // finally
        // {
        //     // 关闭响应
        //     context.Response.Close();
        // }
    }

    List<TaskData> SelectTasks()
    {
        var tasks = new List<TaskData>();
        int toltoken = 0;
        lock (_lockObject)
        {
            foreach (var task in taskDatas)
            {
                if (task.state == TaskData.TaskState.Waiting)
                {
                    if (task.retryCount > 2 && tasks.Count > 0)
                        break;
                    if (tasks.Count > 0 && (tasks[0].retryCount > 0) != (task.retryCount > 0))
                        break;
                    toltoken += task.charLen;
                    tasks.Add(task);
                    if (toltoken >= _maxWordCount)
                        break;
                }
            }
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
        Logger.Debug($"添加任务: {string.Join(", ", texts)}");
        int totalLen = 0;
        foreach (var t in texts) totalLen += t.Length;
        var task = new TaskData() { texts = texts, context = context, charLen = totalLen };
        task.addTick = Environment.TickCount;

        lock (_lockObject)
        {
            taskDatas.Add(task);
            _lastAddTime = Environment.TickCount;
        }

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

    int curProcessingCount = 0;
    private readonly object _lockObject = new object();


    void TaskRespond(TaskData task)
    {
        if (!task.TryRespond())
            Logger.Warn($"响应发送失败: state={task.state} texts[0]={task.texts?[0]}");
        lock (_lockObject)
        {
            taskDatas.Remove(task);
        }
    }
    void ProcessTaskBatch(List<TaskData> tasks)
    {
        int hashkey = tasks.GetHashCode();
        try
        {
            foreach (var task in tasks)
                Logger.Debug($"{hashkey} 翻译开始:{task.texts[0]}");

            List<string> texts = new List<string>();
            foreach (var task in tasks)
                texts.AddRange(task.texts);

            var system = Config.prompt_base
                .Replace("{{TARGET_LAN}}", DestinationLanguage)
                .Replace("{{SOURCE_LAN}}", SourceLanguage);
            if (!string.IsNullOrEmpty(_extraPrompt))
                system += "\n\n" + _extraPrompt;
            var inputJson = BuildInputJson(texts);

            _history.CheckAndClearIfOverLimit(system, inputJson);
            var messages = _history.BuildMessages(system, inputJson);

            var totalChars = texts.Sum(t => t.Length);
            Logger.Info($"批次 {hashkey}: 发送 {texts.Count} 条文本, {totalChars} 字符, 历史{_history.TurnCount}轮, 并行 {curProcessingCount}/{_parallelCount}");

            var result = LlmClient.Translate(_url, _apiKey, _model, messages, _modelParams);

            Logger.Debug($"full流({result.FullResponse.Length}字, {result.ChunkCount}块): {result.FullResponse}");

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
                    Logger.Debug($"{hashkey} 解析结果键越界: 键={kvp.Key} 任务总数={tasks.Count}");
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
                Logger.Debug($"{hashkey} 流OK [{num}]: {rs}");
                i++;
            }
            Logger.Debug($"{hashkey} 解析完成: {i}/{tasks.Count} 条");
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
            Logger.Debug($"翻译结束:{hashkey} curProcessing={curProcessingCount}");
            int retried = 0, failed = 0;
            foreach (var task in tasks)
            {
                if (task.state == TaskData.TaskState.Completed || task.state == TaskData.TaskState.Closed)
                    continue;
                task.retryCount++;
                if (task.retryCount < _maxRetry)
                {
                    Logger.Debug($"重试({task.retryCount}/{_maxRetry}): {task.texts[0]}");
                    task.state = TaskData.TaskState.Waiting;
                    task.result = null;
                    retried++;
                }
                else
                {
                    Logger.Error($"重试耗尽({_maxRetry}次), 放弃: {task.texts[0]}");
                    task.state = TaskData.TaskState.Failed;
                    TaskRespond(task);
                    failed++;
                }
            }
            if (retried > 0 || failed > 0) Logger.Info($"批次 {hashkey}: {retried} 条重试, {failed} 条放弃");
            lock (_lockObject)
                curProcessingCount--;
        }
    }
    //轮询
    public void Polling()
    {
        while (true)
        {
            try
            {

                Thread.Sleep(50);
                if (curProcessingCount >= _parallelCount)
                {
                    continue;
                }
                int waitingCount;
                int waitingToken = 0;
                lock (_lockObject)
                {
                    waitingCount = taskDatas.Count(t => t.state == TaskData.TaskState.Waiting);
                    waitingToken = taskDatas
                        .Where(t => t.state == TaskData.TaskState.Waiting)
                        .Sum(t => t.charLen);
                }
                if (taskDatas.Count > 0)
                    Logger.Debug($"Polling 并行{curProcessingCount}/{_parallelCount} 队列{taskDatas.Count}条");

                if (taskDatas.Count > 200)
                    Logger.Warn($"任务积压严重: {taskDatas.Count} 条，翻译速度可能跟不上文本到达速度");

                // BatchTimeout: 无新文本传入到期后处理, MaxWordCount不受限
                if (waitingCount > 0 && waitingToken < _maxWordCount && Environment.TickCount - _lastAddTime < _batchTimeoutMs)
                {
                    continue;
                }

                if (waitingCount > 0)
                {
                    var idleMs = Environment.TickCount - _lastAddTime;
                    var trigger = waitingToken >= _maxWordCount ? "字数达标" : "超时";
                    Logger.Info($"触发发送: 等待{waitingCount}条 字数{waitingToken}/{_maxWordCount} 空闲{idleMs}ms 触发={trigger}");
                }

                List<List<TaskData>> taskDatass = new List<List<TaskData>>();
                lock (_lockObject)
                {
                    var batch = SelectTasks();
                    while (batch.Count > 0 && curProcessingCount < _parallelCount)
                    {
                        curProcessingCount++;
                        foreach (var task in batch)
                            task.state = TaskData.TaskState.Processing;
                        taskDatass.Add(batch);
                        batch = SelectTasks();
                    }
                }

                if (taskDatass.Count > 0)
                {
                    foreach (var tasklist in taskDatass)
                    {
                        var totalChars = tasklist.Sum(t => t.charLen);
                        Logger.Info($"批次启动: {tasklist.Count}条 {totalChars}字符 并行 {curProcessingCount}/{_parallelCount}");
                        var taskListCopy = new List<TaskData>(tasklist);
                        Thread processingThread = new Thread(() => ProcessTaskBatch(taskListCopy));
                        processingThread.IsBackground = true;
                        processingThread.Start();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
            }
        }
    }
}