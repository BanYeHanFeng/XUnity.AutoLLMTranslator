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
        public int reqID { get; set; }
        public int retryCount { get; set; }
        public TaskState state = TaskState.Waiting;
        public long addTick;

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


    private string[]? _apiKeys;
    private string? _model;
    private string? _requirement;
    private string? _url;
    private string? _terminology;
    private string? _gameName;
    private string? _gameDesc;
    private int _maxWordCount = 2500;
    private int _parallelCount = 10;
    private int _pollingInterval = 1000;
    private string? DestinationLanguage;
    private string? SourceLanguage;
    //使用半角符号
    private bool _halfWidth = true;
    private int _maxRetry = 10;
    private int _batchTimeoutMs = 1000;
    private long _lastAddTime = 0;
    private long _oldestWaitingTick = 0;
    private string _modelParams = "";
    List<TaskData> taskDatas = new List<TaskData>();
    TranslateDB translateDB = new TranslateDB();
    HttpListener listener;
    private int _historyTurns = -1;
    List<object> _conversationHistory = new List<object>();
    readonly object _historyLock = new object();

    private int _currentKeyIndex = 0;
    public void Init(IInitializationContext context)
    {
        _apiKeys = context.GetOrCreateSetting("AutoLLM", "APIKey", "")?.Split(';') ?? new string[] { "NOKEY" };
        _model = context.GetOrCreateSetting("AutoLLM", "Model", "gpt-4o");
        _requirement = context.GetOrCreateSetting("AutoLLM", "Requirement", "");
        _url = context.GetOrCreateSetting("AutoLLM", "URL", "https://api.openai.com/v1/chat/completions");
        _terminology = context.GetOrCreateSetting("AutoLLM", "Terminology", "");
        _gameName = context.GetOrCreateSetting("AutoLLM", "GameName", "A Game");
        _gameDesc = context.GetOrCreateSetting("AutoLLM", "GameDesc", "");
        _maxWordCount = context.GetOrCreateSetting("AutoLLM", "MaxWordCount", 2500);
        _parallelCount = context.GetOrCreateSetting("AutoLLM", "ParallelCount", 3);
        _pollingInterval = context.GetOrCreateSetting("AutoLLM", "Interval", 200);
        _halfWidth = context.GetOrCreateSetting("AutoLLM", "HalfWidth", true);
        _maxRetry = context.GetOrCreateSetting("AutoLLM", "MaxRetry", 10);
        _modelParams = context.GetOrCreateSetting("AutoLLM", "ModelParams", "");
        _batchTimeoutMs = context.GetOrCreateSetting("AutoLLM", "BatchTimeout", 1000);
        _historyTurns = context.GetOrCreateSetting("AutoLLM", "History", -1);
        ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, _parallelCount * 2);
        ServicePointManager.Expect100Continue = false;
        if (context.GetOrCreateSetting("AutoLLM", "DisableSpamChecks", false))
        {
            context.DisableSpamChecks();
        }

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
        if ((_apiKeys?.Length ?? 0) == 0 && !_url.Contains("localhost") && !_url.Contains("127.0.0.1") && !_url.Contains("192.168."))
        {
            throw new Exception("The AutoLLM endpoint requires an API key which has not been provided.");
        }
        translateDB.Init(context, _terminology);

        listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:20000/");
        // 启动监听
        listener.Start();
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
    }

    private void ProcessRequest(HttpListenerContext context)
    {
        try
        {
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
                    var task = AddTask(texts, context);
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
                    int taskToken = 0;
                    foreach (var txt in task.texts)
                    {
                        taskToken += txt.Length;
                    }
                    if (task.retryCount > 2 && tasks.Count > 0)
                    {
                        continue;
                    }
                    toltoken += taskToken;
                    tasks.Add(task);
                    if (task.retryCount > 0)//错过就单独处理
                        break;
                    if (toltoken >= _maxWordCount)
                        break;
                }
            }
        }
        return tasks;
    }

    public TaskData AddTask(string[] texts, HttpListenerContext context)
    {
        Logger.Debug($"添加任务: {string.Join(", ", texts)}");
        var task = new TaskData() { texts = texts, context = context };
        task.addTick = Environment.TickCount;

        lock (_lockObject)
        {
            bool isFirstWaiting = !taskDatas.Any(t => t.state == TaskData.TaskState.Waiting);
            taskDatas.Insert(0, task);
            _lastAddTime = Environment.TickCount;
            if (isFirstWaiting)
                _oldestWaitingTick = task.addTick;
        }

        // // 等待任务完成
        // task.WaitOne();

        // // 移除任务时上锁
        // lock (_lockObject)
        // {
        //     taskDatas.Remove(task);
        // }

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


    private string GetNextApiKey()
    {
        if (_apiKeys == null || _apiKeys.Length == 0)
        {
            return string.Empty;
        }
        lock (_apiKeys)
        {
            var key = _apiKeys[_currentKeyIndex];
            _currentKeyIndex = (_currentKeyIndex + 1) % _apiKeys.Length;
            return key;
        }
    }

    void TaskRespond(TaskData task)
    {
        task.TryRespond();
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
            //Log($"翻译开始Batch:" + hashkey);
            foreach (var task in tasks)
            {
                Logger.Debug($"{hashkey} 翻译开始:{task.texts[0]}");
            }
            List<string> texts = new List<string>();
            foreach (var task in tasks)
            {
                texts.AddRange(task.texts);
            }
            var system = Config.prompt_base
            .Replace("{{GAMENAME}}", _gameName)
            .Replace("{{GAMEDESC}}", _gameDesc)
            .Replace("{{OTHER}}", _requirement)
            .Replace("{{TARGET_LAN}}", DestinationLanguage)
            .Replace("{{SOURCE_LAN}}", SourceLanguage);
            var inputJson = BuildInputJson(texts);

            var messages = new List<object>();
            messages.Add(new { role = "system", content = system });
            lock (_historyLock)
            {
                foreach (var msg in _conversationHistory)
                    messages.Add(msg);
            }
            messages.Add(new { role = "user", content = inputJson });

            var requestBody = new Dictionary<string, object>
            {
                { "model", _model },
                { "messages", messages },
                { "response_format", new Dictionary<string, object> { { "type", "json_object" } } }
            };
            if (!string.IsNullOrEmpty(_modelParams))
            {
                try
                {
                    var modelParamsData = SimpleJson.ParseModelParams(_modelParams);
                    foreach (var item in modelParamsData)
                        requestBody[item.Key] = item.Value;
                }
                catch (Exception ex)
                {
                    Logger.Error($"模型参数解析错误: {ex.Message}");
                }
            }

            var totalChars = texts.Sum(t => t.Length);
            Logger.Info($"批次 {hashkey}: 发送 {texts.Count} 条文本, {totalChars} 字符, 并行 {curProcessingCount}/{_parallelCount}");



            // 创建HTTP请求
            var request = (HttpWebRequest)WebRequest.Create(_url);
            request.Method = "POST";
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            request.Headers.Add("Authorization", $"Bearer {GetNextApiKey()}");
            request.ContentType = "application/json";

            // 写入请求体
            requestBody.Add("stream", true);
            var requestJson = SimpleJson.Serialize(requestBody);
            //Log($"请求: {requestJson}");
            using (var streamWriter = new StreamWriter(request.GetRequestStream()))
            {
                streamWriter.Write(requestJson);
            }

            // 获取响应
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                var fullResponse = "";
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data: ")) continue;

                    var data = line.Substring(6);
                    if (data == "[DONE]") break;

                    try
                    {
                        var content = SimpleJson.ParseSseContent(data);
                        if (!string.IsNullOrEmpty(content))
                            fullResponse += content;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"解析流响应出错: {ex.Message}");
                    }
                }

                Logger.Debug($"full流: {fullResponse}");

                if (string.IsNullOrEmpty(fullResponse))
                    throw new Exception("翻译结果为空");

                var resultObj = SimpleJson.ParseJsonObject(fullResponse);
                if (resultObj == null || resultObj.Count == 0)
                    throw new Exception($"JSON结果解析失败: {fullResponse}");

                int i = 0;
                foreach (var kvp in resultObj)
                {
                    int num;
                    if (!int.TryParse(kvp.Key, out num)) continue;
                    if (num < 1 || num > tasks.Count) continue;

                    var rs = kvp.Value as string;
                    if (string.IsNullOrEmpty(rs)) continue;

                    if (_halfWidth)
                    {
                        rs = Regex.Replace(rs, @"[！＂＃＄％＆＇（）＊＋，－．／０１２３４５６７８９：；＜＝＞？＠［＼］＾＿｀｛｜｝～]", m => ((char)(m.Value[0] - 0xFEE0)).ToString());
                    }

                    var task = tasks[num - 1];
                    task.result = new string[] { translateDB.FindTerminology(task.texts[0]) ?? rs };
                    task.state = TaskData.TaskState.Completed;
                    TaskRespond(task);
                    Logger.Debug($"{hashkey} 流OK: {rs}");
                    i++;
                }

                if (_historyTurns != 0)
                {
                    lock (_historyLock)
                    {
                        _conversationHistory.Add(new { role = "user", content = inputJson });
                        _conversationHistory.Add(new { role = "assistant", content = fullResponse });
                        TrimHistory();
                    }
                }
            }

        }
        catch (WebException ex)
        {
            Logger.Error($"翻译失败: {ex.Message}");
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
        }
        catch (Exception ex)
        {
            Logger.Error($"翻译失败: {ex.Message}");
        }
        finally
        {
            Logger.Debug($"翻译结束:" + hashkey);
            foreach (var task in tasks)
            {
                //失败了重新翻译
                if (task.state != TaskData.TaskState.Completed)
                {
                    task.retryCount++;
                    if (task.retryCount < _maxRetry)
                    {
                        Logger.Debug($"重新翻译:" + task.texts[0]);
                        task.state = TaskData.TaskState.Waiting;
                        task.result = null;
                    }
                    else
                    {
                        Logger.Error($"重试翻译依然失败，没救了:" + task.texts[0]);
                        task.state = TaskData.TaskState.Failed;
                        TaskRespond(task);
                    }
                }
            }
            lock (_lockObject)
                curProcessingCount--;
        }
    }

    void TrimHistory()
    {
        if (_historyTurns > 0)
        {
            int maxPairs = _historyTurns;
            while (_conversationHistory.Count > maxPairs * 2)
                _conversationHistory.RemoveAt(0);
        }
    }

    //轮询
    public void Polling()
    {
        while (true)
        {
            try
            {

                Thread.Sleep(_pollingInterval);
                Logger.Debug($"Polling curProcessingCount: {curProcessingCount}/{_parallelCount} TASKS: {taskDatas.Count}");
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
                        .SelectMany(t => t.texts)
                        .Sum(txt => txt.Length);
                }

                // BatchTimeout: 无新文本传入到期后处理, MaxWordCount不受限
                if (waitingCount > 0 && waitingToken < _maxWordCount && Environment.TickCount - _lastAddTime < _batchTimeoutMs)
                {
                    continue;
                }

                if (waitingCount > 0)
                    Logger.Info($"触发发送: waiting={waitingCount} tokens={waitingToken}/{_maxWordCount} idleMs={Environment.TickCount - _lastAddTime}");

                List<List<TaskData>> taskDatass = new List<List<TaskData>>();
                lock (_lockObject)
                {
                    var batch = SelectTasks();
                    while (batch.Count > 0 && taskDatass.Count < _parallelCount)
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
                        var totalChars = tasklist.Sum(t => t.texts.Sum(txt => txt.Length));
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