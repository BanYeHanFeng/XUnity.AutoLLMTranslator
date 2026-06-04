# XUnity.AutoLLMTranslator 重构实施方案

## 零、设计原则

1. **消除根因而非修补**：HTTP 代理层是为了绕过框架限制而引入的 hack，重构必须从架构层面解决，直接实现 `ITranslateEndpoint`
2. **接口最小化**：仅为需要 mock 的外部依赖（LLM API）提供接口，其余全部为具体类，避免无意义的 DI 容器
3. **职责单一**：每个类只做一件事，用类名表达其唯一职责，禁止使用 `Helper`/`Manager`/`Utils` 等模糊后缀
4. **删除优先于保留**：旧代码不保留为 `_v2`/`_old`/`#if` 分支，确认无用即删除；不存在"以后可能用到"的代码
5. **net35 是硬约束**：所有 API 必须在 .NET Framework 3.5 子集内，不使用 `async/await`、`HttpClient`、`BlockingCollection<T>`、`Channel<T>`
6. **无兼容层**：不引入适配器模式或向后兼容桥接代码，新架构直接替换旧架构

---

## 一、数据流（端到端）

```
XUnity.AutoTranslator 框架
  │  ITranslateEndpoint.Translate(context)   ← Unity 主线程协程
  ▼
AutoLLMTranslateEndpoint
  │  创建 TranslationTask{UntranslatedText, Context}
  │  入队到 TranslationOrchestrator._taskQueue
  │  协程轮询 task.State 直到 Completed 或 Failed
  │  调用 context.Complete(translated) 或 context.Fail(error)
  ▼
TranslationOrchestrator (后台线程)
  │  WorkerLoop: WaitOne(50ms) → RateLimitGuard检查 → BatchSelector取批
  │  ProcessBatch:
  │    1. 合并 batch 文本为 {"1":"...","2":"..."} JSON
  │    2. ConversationHistory.CheckAndClearIfOverLimit
  │    3. 构建 LlmRequest{messages = [system, ...history, user]}
  │    4. ILlmClient.Translate(...) → 同步等待 LLM 返回 → LlmResult
  │    5. ParseJsonObject → 分发结果 → 标记 Completed
  │    6. 异常: WebException(429) → 限速重试, 其他 → RetryHandler 判定 → 重新入队或标记 Failed
  ▼
ILlmClient (HttpWebRequest + SSE 流解析)
  │  POST → OpenAI 兼容 API
  │  逐行读取 SSE → 累积 FullResponse → 提取 usage
  ▼
LLM Provider (OpenAI / 兼容服务)
```

---

## 二、文件清单（全量）

### 2.1 新建文件（13 个）

```
Endpoint/
  AutoLLMTranslateEndpoint.cs       # ITranslateEndpoint 实现 (~60行)

Orchestration/
  TranslationOrchestrator.cs        # 核心协调器 (~200行)
  TaskQueue.cs                      # 线程安全任务队列 (~100行)
  RetryHandler.cs                   # 重试策略 (~30行)
  RateLimitGuard.cs                 # 限速退避 (~50行)

Translation/
  ILlmClient.cs                     # LLM 客户端接口 (~10行)
  LlmClient.cs                      # HttpWebRequest SSE 实现 (~120行)
  ConversationHistory.cs            # 对话历史管理 (~60行)

Configuration/
  AutoLLMConfig.cs                  # 类型化配置 (~60行)
  PromptManager.cs                  # 系统提示词加载 (~60行)

Models/
  LlmModels.cs                      # LlmMessage, LlmResult, LlmUsage (~40行)
  TranslationTask.cs                # 翻译任务数据+状态机 (~80行)

Prompt.cs                           # 原 Config.cs 重命名 (~15行)
```

### 2.2 修改文件（2 个）

```
SimpleJson.cs                       # 删除 SerializeTexts/ParseTexts，保留其余 (~200行)
Logger.cs                           # 删除 INI 解析器，日志等级由 AutoLLMConfig 传入 (~80行)
```

### 2.3 删除文件（2 个）

```
AutoLLMTranslatorEndpoint.cs        # 旧的 WwwEndpoint 实现 (52行)
TranslatorTask.cs                   # 旧的巨型单体类 (663行)
```

### 2.4 不动文件

```
XUnity.AutoLLMTranslator.csproj     # 保持 net35，仅调整编译文件列表
XUnity.AutoLLMTranslator.sln        # 不动
README.md / README.en.md            # 不动
LICENSE.txt                         # 不动
packages/                           # 不动
.github/                            # 不动
.gitignore / .gitattributes         # 不动
```

---

## 三、逐文件规格定义

### 3.1 `Prompt.cs`（重命名自 Config.cs）

```csharp
internal static class Prompt
{
    // 与 Config.PromptBase 完全一致，不修改
    public const string Default = @"你是一位专业的游戏文本翻译专家...";
}
```

引用方式：`Prompt.Default` 替代 `Config.PromptBase`。

---

### 3.2 `Models/LlmModels.cs`

```csharp
internal class LlmMessage
{
    public string Role { get; set; }      // "system" | "user" | "assistant"
    public string Content { get; set; }
}

internal class LlmResult
{
    public string FullResponse { get; set; }
    public LlmUsage Usage { get; set; }
    public int ChunkCount { get; set; }
    public bool DoneReceived { get; set; }
    public long ElapsedMs { get; set; }
}

internal class LlmUsage
{
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
}
```

说明：全部 `internal`，仅插件内部使用。不需要接口或 `INotifyPropertyChanged`。

---

### 3.3 `Models/TranslationTask.cs`

```csharp
internal enum TaskState { Waiting, Processing, Completed, Failed }

internal class TranslationTask
{
    // 输入（创建时设置）
    public string UntranslatedText { get; set; }

    // 输出（完成时设置）
    public string TranslatedText { get; set; }
    public string ErrorMessage { get; set; }

    // 状态机
    public TaskState State { get; set; }
    public int RetryCount { get; set; }
    public int CharLen { get; set; }                         // UntranslatedText.Length

    // 时间戳
    public long CreatedTick { get; set; }                     // Environment.TickCount

    // 协程等待（endpoint 用此字段轮询完成状态）
    public volatile bool IsCompleted;

    // 便利方法
    public void MarkCompleted(string translated)
    {
        TranslatedText = translated;
        State = TaskState.Completed;
        IsCompleted = true;
    }

    public void MarkFailed(string error)
    {
        ErrorMessage = error;
        State = TaskState.Failed;
        IsCompleted = true;
    }

    public void ResetForRetry()
    {
        State = TaskState.Waiting;
        TranslatedText = null;
        ErrorMessage = null;
    }
}
```

线程安全约定：
- `State` 和 `IsCompleted` 的写入在 `TaskQueue._lock` 内进行
- `IsCompleted` 额外标记 `volatile`，允许协程在主线程无锁读取
- `RetryCount` 在 `TaskQueue._lock` 内递增（重试入队时）

---

### 3.4 `Configuration/AutoLLMConfig.cs`

```csharp
internal class AutoLLMConfig
{
    public string Model { get; set; } = "";
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int MaxWordCount { get; set; } = 2500;
    public int ParallelCount { get; set; } = 1;
    public int MaxRetry { get; set; } = 10;
    public int MaxContext { get; set; } = 1024;
    public string ModelParams { get; set; } = "";
    public bool CustomPrompt { get; set; } = false;
    public bool HalfWidth { get; set; } = true;
    public bool DisableSpamChecks { get; set; } = true;
    public string BepInExRoot { get; set; }
    public string SourceLanguage { get; set; }
    public string DestinationLanguage { get; set; }
    public Dictionary<string, object> ParsedModelParams { get; set; }
    public string CachedSystemPrompt { get; set; }

    // 日志等级
    public bool InfoEnabled { get; set; } = true;
    public bool WarnEnabled { get; set; } = true;
    public bool DebugEnabled { get; set; } = false;

    /// <summary>配置是否有效（Model 和 URL 均已填写）。</summary>
    public bool IsValid => !string.IsNullOrEmpty(Model) && !string.IsNullOrEmpty(Url);
}
```

工厂方法签名：
```csharp
public static AutoLLMConfig FromInitializationContext(IInitializationContext context)
```

实现要点：
1. 从 `context.GetOrCreateSetting("AutoLLM", key, default)` 读取所有配置
2. `ParsedModelParams = SimpleJson.ParseModelParams(ModelParams)` （预解析一次）
3. `BepInExRoot` 定位逻辑：从 `context.TranslatorDirectory` 向上查找含 `core/` 或名为 `BepInEx` 的目录（同上 "P1" 逻辑）
4. `CachedSystemPrompt` 由 `PromptManager.Build(config)` 填充
5. `Url` 自动补尾：若以 `/v1` 结尾追加 `/chat/completions`，若以 `/v1/` 结尾追加 `chat/completions`
6. 日志等级从 `BepInEx.cfg` 读取（将现有 Logger 中的 INI 解析逻辑移入此方法）
7. `DisableSpamChecks` 读取后立即调用 `context.DisableSpamChecks()`
8. ThreadPool 扩容：`ThreadPool.SetMinThreads` 确保 worker >= `ParallelCount + 2`
9. `ServicePointManager.DefaultConnectionLimit = Math.Max(DefaultConnectionLimit, ParallelCount * 2)`
10. `ServicePointManager.Expect100Continue = false`

验证：`Model` 或 `Url` 为空时不做 init → `IsValid` 属性返回 false。

---

### 3.5 `Configuration/PromptManager.cs`

```csharp
internal static class PromptManager
{
    /// <summary>
    /// 返回已替换 {{SOURCE_LAN}} 和 {{TARGET_LAN}} 的系统提示词。
    /// config.CachedSystemPrompt 应存储此返回值。
    /// </summary>
    public static string Build(AutoLLMConfig config)
    {
        string basePrompt;
        if (!config.CustomPrompt)
        {
            basePrompt = Prompt.Default;
        }
        else
        {
            var path = Path.Combine(config.BepInExRoot, "config", "AutoLLM_CustomPrompt.txt");
            if (File.Exists(path))
            {
                try { basePrompt = File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception ex)
                {
                    Logger.Error($"读取自定义提示词失败: {ex}");
                    basePrompt = Prompt.Default;
                }
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(config.BepInExRoot, "config"));
                    File.WriteAllText(path, Prompt.Default, Encoding.UTF8);
                    Logger.Info($"已创建默认自定义提示词: {path}");
                    basePrompt = Prompt.Default;
                }
                catch (Exception ex)
                {
                    Logger.Error($"创建自定义提示词失败: {ex}");
                    basePrompt = Prompt.Default;
                }
            }
        }
        return basePrompt
            .Replace("{{SOURCE_LAN}}", config.SourceLanguage)
            .Replace("{{TARGET_LAN}}", config.DestinationLanguage);
    }
}
```

---

### 3.6 `Translation/ILlmClient.cs`

```csharp
internal interface ILlmClient
{
    /// <summary>
    /// 同步发送翻译请求到 LLM API，返回结果或抛出异常。
    /// 调用线程会被阻塞（HttpWebRequest 是同步的），由调用方负责在后台线程上调用。
    /// </summary>
    /// <throws>WebException（网络/HTTP 错误，含 429）</throws>
    /// <throws>Exception（解析失败等其他错误）</throws>
    LlmResult Translate(
        string url,
        string apiKey,
        string model,
        List<LlmMessage> messages,
        Dictionary<string, object> extraParams);
}
```

说明：
- 同步接口：net35 下 HttpWebRequest 本身是阻塞的，回调模式无实际收益
- 调用方（TranslationOrchestrator.ProcessBatch）在 ThreadPool 线程上调用，阻塞不影响主线程
- 接口存在唯一目的：允许单元测试中 mock LLM API 响应

---

### 3.7 `Translation/LlmClient.cs`

```csharp
internal class LlmClient : ILlmClient
{
    // 单例标志（static：跨批次共享）
    private static bool _warnedUsageMissing = false;
    private static bool _cacheStatsSupported = false;
    private static bool _cacheStatsChecked = false;

    public static bool CacheStatsSupported => _cacheStatsSupported;

    public LlmResult Translate(
        string url, string apiKey, string model,
        List<LlmMessage> messages,
        Dictionary<string, object> extraParams)
    {
        // 1. 构建请求体（与原始 LlmClient.Translate 完全一致）
        var requestBody = new Dictionary<string, object>();
        foreach (var kv in extraParams)
            requestBody[kv.Key] = kv.Value;
        requestBody["model"] = model;
        requestBody["messages"] = SerializeMessages(messages);
        requestBody["response_format"] = new Dictionary<string, object> { { "type", "json_object" } };
        requestBody["stream"] = true;
        requestBody["stream_options"] = new Dictionary<string, object> { { "include_usage", true } };
        string requestJson = SimpleJson.Serialize(requestBody);

        // 2. 发送 HttpWebRequest（Timeout=600000, ReadWriteTimeout=120000）
        var httpRequest = (HttpWebRequest)WebRequest.Create(url);
        httpRequest.Method = "POST";
        httpRequest.Timeout = 600000;
        httpRequest.ReadWriteTimeout = 120000;
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Add("Authorization", "Bearer " + apiKey);
        httpRequest.ContentType = "application/json";

        using (var sw = new StreamWriter(httpRequest.GetRequestStream()))
            sw.Write(requestJson);

        long startTick = Environment.TickCount;

        // 3. 读取 SSE 流（与原始代码完全一致的逐行解析逻辑）
        using (var response = (HttpWebResponse)httpRequest.GetResponse())
        using (var stream = response.GetResponseStream())
        using (var reader = new StreamReader(stream))
        {
            var fullResponse = new StringBuilder();
            var usage = new Dictionary<string, object>();
            int chunks = 0;
            bool done = false;
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data: ")) continue;
                string data = line.Substring(6);
                if (data == "[DONE]") { done = true; break; }
                chunks++;
                SimpleJson.ParseSseChunk(data, out string content, out Dictionary<string, object> u);
                if (!string.IsNullOrEmpty(content))
                    fullResponse.Append(content);
                if (u != null) usage = u;
            }

            if (!done && fullResponse.Length > 0)
                Logger.Warn("SSE 流未收到 [DONE] (chunks=" + chunks + ")");

            // 4. 构建结果
            var result = new LlmResult
            {
                FullResponse = fullResponse.ToString(),
                ChunkCount = chunks,
                DoneReceived = done,
                ElapsedMs = Environment.TickCount - startTick
            };

            // 5. 提取 token 用量
            ExtractUsage(usage, result);

            return result;
        }
    }

    /// <summary>将 LlmMessage 列表转为 SimpleJson 可序列化的 List&lt;Dictionary&gt;。</summary>
    private static List<Dictionary<string, object>> SerializeMessages(List<LlmMessage> messages)
    {
        var list = new List<Dictionary<string, object>>();
        foreach (var msg in messages)
        {
            list.Add(new Dictionary<string, object>
            {
                { "role", msg.Role },
                { "content", msg.Content }
            });
        }
        return list;
    }

    /// <summary>从 usage dict 提取 token 统计（与原始代码逻辑完全一致）。</summary>
    private static void ExtractUsage(Dictionary<string, object> usage, LlmResult result)
    {
        if (usage.ContainsKey("prompt_tokens"))
        {
            result.Usage = new LlmUsage();
            result.Usage.PromptTokens = Convert.ToInt64(usage["prompt_tokens"]);
            result.Usage.CompletionTokens = usage.ContainsKey("completion_tokens")
                ? Convert.ToInt64(usage["completion_tokens"]) : 0;

            if (!_cacheStatsChecked)
            {
                _cacheStatsChecked = true;
                _cacheStatsSupported = usage.ContainsKey("prompt_cache_hit_tokens")
                    || usage.ContainsKey("prompt_cache_miss_tokens");
                if (!_cacheStatsSupported)
                    Logger.Info("API 流式响应不返回缓存命中/未中统计");
            }

            if (_cacheStatsSupported)
            {
                if (usage.TryGetValue("prompt_cache_hit_tokens", out object hit))
                    result.Usage.CacheHitTokens = Convert.ToInt64(hit);
                if (usage.TryGetValue("prompt_cache_miss_tokens", out object miss))
                    result.Usage.CacheMissTokens = Convert.ToInt64(miss);
            }
        }
        else if (!_warnedUsageMissing)
        {
            Logger.Debug("usage 字段未返回，API 可能不支持 token 统计");
            _warnedUsageMissing = true;
        }
    }
}
```

说明：
- `SerializeMessages`：将强类型 `LlmMessage` 列表转为 `List<Dictionary<string,object>>`，保持与原本 `List<object>` 相同的 JSON 输出格式
- `ExtractUsage`：静态方法，方便单元测试独立验证 token 提取逻辑
- 缓存统计检测逻辑（`_cacheStatsChecked`/`_cacheStatsSupported`）与原代码逐行一致

---

### 3.8 `Translation/ConversationHistory.cs`

```csharp
internal class ConversationHistory
{
    private readonly List<LlmMessage> _history = new List<LlmMessage>();
    private readonly object _lock = new object();
    private int _cachedHistoryChars = 0;
    private int _clearCount = 0;

    public bool Enabled { get; set; }
    public int MaxContext { get; set; }
    public int TurnCount { get { lock (_lock) return _history.Count / 2; } }

    /// <summary>构建完整消息列表：[system, ...history, user]。</summary>
    public List<LlmMessage> BuildMessages(string systemPrompt, string userInput)
    {
        var messages = new List<LlmMessage>();
        messages.Add(new LlmMessage { Role = "system", Content = systemPrompt });
        lock (_lock)
        {
            foreach (var msg in _history)
                messages.Add(msg);
        }
        messages.Add(new LlmMessage { Role = "user", Content = userInput });
        return messages;
    }

    /// <summary>追加一轮对话（user + assistant）。</summary>
    public void AppendExchange(string userInput, string assistantOutput)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            _history.Add(new LlmMessage { Role = "user", Content = userInput });
            _history.Add(new LlmMessage { Role = "assistant", Content = assistantOutput });
            _cachedHistoryChars += userInput.Length + assistantOutput.Length;
        }
    }

    /// <summary>检查上下文是否超限，超限则清空历史。</summary>
    public void CheckAndClearIfOverLimit(string systemPrompt, string userInput)
    {
        if (MaxContext <= 0 || !Enabled) return;
        lock (_lock)
        {
            int chars = systemPrompt.Length + userInput.Length + _cachedHistoryChars;
            int estimatedTokens = chars / 2;   // 粗估：2字符≈1token
            if (Logger.IsDebugEnabled)
                Logger.Debug($"上下文估算: {estimatedTokens}/{MaxContext} tokens " +
                    $"(字符{chars}, 历史{_history.Count / 2}轮)");
            if (estimatedTokens > MaxContext)
            {
                _history.Clear();
                _cachedHistoryChars = 0;
                _clearCount++;
                Logger.Info($"历史超出 MaxContext({MaxContext})，已清空对话历史（第{_clearCount}次）");
            }
        }
    }
}
```

变更：
- `List<object>` → `List<LlmMessage>`
- `Dictionary<string, object>` 消息构建 → `new LlmMessage{Role, Content}`
- 其余逻辑（锁、token 估算、超限清空）不变

---

### 3.9 `Orchestration/TaskQueue.cs`

```csharp
internal class TaskQueue
{
    private readonly Queue<TranslationTask> _queue = new Queue<TranslationTask>();
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);
    private readonly object _lock = new object();
    private readonly int _maxSize;

    private int _waitingTotalChars = 0;
    private int _outstandingCount = 0;    // 队列中+处理中的总数

    public int Count { get { lock (_lock) return _queue.Count; } }
    public int WaitingTotalChars { get { lock (_lock) return _waitingTotalChars; } }
    public int OutstandingCount { get { return _outstandingCount; } } // volatile read OK
    public AutoResetEvent Signal => _signal;

    public TaskQueue(int maxSize = 2000)
    {
        _maxSize = maxSize;
    }

    /// <summary>入队。队列满时返回 false，任务未被入队。</summary>
    public bool TryEnqueue(TranslationTask task)
    {
        lock (_lock)
        {
            if (_outstandingCount >= _maxSize)
                return false;
            _queue.Enqueue(task);
            _waitingTotalChars += task.CharLen;
            _outstandingCount++;
        }
        _signal.Set();
        return true;
    }

    /// <summary>
    /// 从队列头部取一批任务。
    /// 规则：不混搭重试/非重试任务，retryCount>2 单独成批，字数超限时截断但至少保留 1 条。
    /// 返回的 batch 从队列中移除，_waitingTotalChars 同步扣减。
    /// </summary>
    public List<TranslationTask> DequeueBatch(int maxChars)
    {
        var batch = new List<TranslationTask>();
        int totalChars = 0;
        lock (_lock)
        {
            int count = _queue.Count;
            while (count > 0)
            {
                var task = _queue.Peek();

                // 不混搭规则
                if (batch.Count > 0)
                {
                    if ((batch[0].RetryCount > 0) != (task.RetryCount > 0))
                        break;
                    if (task.RetryCount > 2)
                        break;
                }

                // 字数上限（至少保1条）
                if (totalChars + task.CharLen > maxChars && batch.Count > 0)
                    break;

                _queue.Dequeue();
                batch.Add(task);
                totalChars += task.CharLen;
                count--;
            }
            if (batch.Count > 0)
                _waitingTotalChars -= totalChars;
        }
        return batch;
    }

    /// <summary>重试时重新入队（不增加 _outstandingCount）。</summary>
    public void ReEnqueue(TranslationTask task)
    {
        lock (_lock)
        {
            task.ResetForRetry();
            _queue.Enqueue(task);
            _waitingTotalChars += task.CharLen;
        }
        _signal.Set();
    }

    /// <summary>任务完成时调用，递减 _outstandingCount。</summary>
    public void MarkCompleted()
    {
        Interlocked.Decrement(ref _outstandingCount);
    }
}
```

线程安全约定：
- `_queue` 和 `_waitingTotalChars` 仅在 `_lock` 内访问
- `_outstandingCount` 读取无需锁（int 原子），写入：Enqueue 时锁内 `++`，MarkCompleted 时 `Interlocked.Decrement`
- `_signal` 在锁外 Set，避免死锁

---

### 3.10 `Orchestration/RetryHandler.cs`

```csharp
internal class RetryHandler
{
    private readonly int _maxRetry;

    public RetryHandler(int maxRetry)
    {
        _maxRetry = maxRetry;
    }

    /// <summary>判断任务是否应重试。</summary>
    public bool ShouldRetry(TranslationTask task)
    {
        return task.RetryCount < _maxRetry;
    }

    /// <summary>递增重试计数（调用方在 ShouldRetry 返回 true 后调用）。</summary>
    public void IncrementRetry(TranslationTask task)
    {
        task.RetryCount++;
    }
}
```

---

### 3.11 `Orchestration/RateLimitGuard.cs`

```csharp
internal class RateLimitGuard
{
    private int _delayMs = 0;          // 当前退避延迟
    private int _cooldownStart = 0;    // Environment.TickCount when cooldown started

    private const int InitialDelayMs = 5000;
    private const int MaxDelayMs = 60000;

    /// <summary>限速时调用，启动/加长退避。</summary>
    public void OnRateLimited()
    {
        _delayMs = _delayMs == 0 ? InitialDelayMs : Math.Min(_delayMs * 2, MaxDelayMs);
        _cooldownStart = Environment.TickCount;
    }

    /// <summary>非限速错误时调用，重置退避。</summary>
    public void Reset()
    {
        _delayMs = 0;
        _cooldownStart = 0;
    }

    /// <summary>当前是否处于退避冷却期。</summary>
    public bool IsBlocked()
    {
        if (_delayMs == 0 || _cooldownStart == 0)
            return false;
        int elapsed = unchecked(Environment.TickCount - _cooldownStart);
        return elapsed < _delayMs;
    }

    /// <summary>当前退避延迟（日志用），单位毫秒。</summary>
    public int CurrentDelayMs => _delayMs;
}
```

注意：`Environment.TickCount` 溢出（约 49.7 天后归零）通过 `unchecked` 减法安全处理。

---

### 3.12 `Orchestration/TranslationOrchestrator.cs`

```csharp
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
                Logger.Warn($"任务积压严重: {outstanding} 条");

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
            Logger.Info($"批次 {batchId}: 发送 {texts.Count} 条, {totalChars} 字符, " +
                $"排队{waitMs}ms, 历史{_history.TurnCount}轮, " +
                $"并行{_processingCount}/{_config.ParallelCount}");

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
                Logger.Info($"LLM usage: 入{result.Usage.PromptTokens} 出{result.Usage.CompletionTokens} " +
                    $"命中{result.Usage.CacheHitTokens} 未中{result.Usage.CacheMissTokens} | " +
                    $"累计: 入{_totalInputTokens} 出{_totalOutputTokens} 命中{_totalCacheHitTokens} 未中{_totalCacheMissTokens}");
            }
            else
            {
                Logger.Info($"LLM usage: 入{result.Usage?.PromptTokens ?? 0} 出{result.Usage?.CompletionTokens ?? 0} | " +
                    $"累计: 入{_totalInputTokens} 出{_totalOutputTokens}");
            }

            if (result.ElapsedMs > 0 && (result.Usage?.CompletionTokens ?? 0) > 0)
                Logger.Info($"LLM 速度: {result.Usage.CompletionTokens * 1000 / result.ElapsedMs} tok/s, 耗时{result.ElapsedMs}ms");

            if (string.IsNullOrEmpty(result.FullResponse))
                throw new Exception("翻译结果为空");

            // 解析响应
            var resultObj = SimpleJson.ParseJsonObject(result.FullResponse);
            if (resultObj == null || resultObj.Count == 0)
                throw new Exception($"JSON结果解析失败: {result.FullResponse}");

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
                Logger.Warn($"批次 {batchId}: 解析不完整, 期望{batch.Count}条 实际{completed}条");
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
                        Logger.Error($"服务器错误响应: {errorText}");
                    }
                }
                catch { }
            }

            if (statusCode == 429)
            {
                isRateLimit = true;
                _rateLimitGuard.OnRateLimited();
                Logger.Warn($"限速退避: {_rateLimitGuard.CurrentDelayMs / 1000}s");
            }
            else
            {
                _rateLimitGuard.Reset();
            }
            Logger.Error($"翻译失败 [{statusCode}]: {we.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error($"翻译失败: {ex.Message}");
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
                    Logger.Info($"批次 {batchId}: 限速重试 {batch.Count} 条（不消耗重试次数）");
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
                        Logger.Error($"重试耗尽({_config.MaxRetry}次), 放弃: {task.UntranslatedText}");
                        task.MarkFailed($"翻译失败，已重试{_config.MaxRetry}次");
                        _taskQueue.MarkCompleted();
                        failed++;
                    }
                }
                if (retried > 0 || failed > 0)
                    Logger.Info($"批次 {batchId}: {retried} 条重试, {failed} 条放弃");
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
```

关键设计决策：
1. `ILlmClient.Translate()` 是同步调用，阻塞 ThreadPool 线程直到 LLM 返回——net35 下 HttpWebRequest 本身是同步的，无需异步抽象
2. 线程模型：WorkerLoop 在专用后台线程运行；ProcessBatch 在 ThreadPool 线程运行；Translate 协程在 Unity 主线程运行
3. `_processingCount`：volatile int，在 WorkerLoop 中读取，在 ProcessBatch finally 中递减。因为 net35 无 `Interlocked` 的 add/sub 语义明确性，使用 volatile + 单一写入点保证正确性
4. Task 状态在 ProcessBatch 内直接设置：任务已从队列出队，无多线程竞争

---

### 3.13 `Endpoint/AutoLLMTranslateEndpoint.cs`

```csharp
internal class AutoLLMTranslateEndpoint : ITranslateEndpoint
{
    private TranslationOrchestrator _orchestrator;
    private bool _initialized;

    // ---- ITranslateEndpoint 成员 ----

    public string Id => "AutoLLMTranslate";
    public string FriendlyName => "AutoLLM Translate";

    // 框架调度参数：由内部队列管理，框架不做批量/并发限制
    public int MaxTranslationsPerRequest => 1;
    public int MaxConcurrency => 500;

    public void Initialize(IInitializationContext context)
    {
        context.SetTranslationDelay(0.1f);

        var config = AutoLLMConfig.FromInitializationContext(context);
        if (!config.IsValid)
        {
            Logger.Error("Model 或 URL 未配置，翻译功能已禁用");
            return;
        }

        Logger.Init(config);
        Logger.Info("端点初始化完成");

        _orchestrator = new TranslationOrchestrator(config, new LlmClient());
        _orchestrator.Start();
        _initialized = true;

        Logger.Info($"已启动 | Model={config.Model} URL={config.Url} " +
            $"MaxWordCount={config.MaxWordCount} ParallelCount={config.ParallelCount}");
    }

    public IEnumerator Translate(ITranslationContext context)
    {
        if (!_initialized)
        {
            context.Fail("端点未初始化");
            yield break;
        }

        if (string.IsNullOrEmpty(context.UntranslatedText))
        {
            if (Logger.IsDebugEnabled) Logger.Debug("翻译请求: 空文本，跳过");
            yield break;
        }

        if (Logger.IsDebugEnabled) Logger.Debug($"翻译请求: {context.UntranslatedText}");

        var task = new TranslationTask
        {
            UntranslatedText = context.UntranslatedText,
            CharLen = context.UntranslatedText.Length,
            CreatedTick = Environment.TickCount,
        };

        if (!_orchestrator.Queue.TryEnqueue(task))
        {
            context.Fail("任务队列已满");
            yield break;
        }

        // 协程轮询，等待任务完成
        while (!task.IsCompleted)
            yield return null;   // Unity 主线程每帧检查

        if (task.State == TaskState.Completed)
            context.Complete(task.TranslatedText);
        else
            context.Fail(task.ErrorMessage ?? "翻译失败");
    }

    // ---- 清理 ----
    public void Dispose()
    {
        _orchestrator?.Shutdown();
    }
}
```

关键设计：
- `Translate()` 作为 Unity 协程，在主线程上执行
- 轮询 `task.IsCompleted` 而非事件回调，确保 `context.Complete()`/`context.Fail()` 在主线程调用（Unity UI 安全）
- `MaxConcurrency = 500`：框架允许 500 个并发 `Translate()` 调用，实际由内部队列控制并发

---

### 3.14 `SimpleJson.cs`（修改）

删除以下方法（仅 HTTP 代理层使用，不再需要）：
- ~~`public static string SerializeTexts(string[] texts)`~~ — 删除
- ~~`public static string[] ParseTexts(string json)`~~ — 删除

保留以下方法（未修改）：
- `public static string Serialize(object obj)`
- `public static Dictionary<string, object> ParseModelParams(string json)`
- `public static Dictionary<string, object> ParseJsonObject(string json)`
- `public static void ParseSseChunk(string json, out string content, out Dictionary<string, object> usage)`
- 所有 private 方法保持不变

---

### 3.15 `Logger.cs`（修改）

删除：
- `Init(string bepinExRoot)` 中的 INI 解析方法（`ParseIniFile`, `ContainsLevel`, `GetBoolValue`）

新增：
```csharp
public static void Init(AutoLLMConfig config)
{
    _debugEnabled = config.DebugEnabled;
    _infoEnabled = config.InfoEnabled;
    _warnEnabled = config.WarnEnabled;
}
```

保留：
- `Info/Debug/Warn/Error` 方法签名不变
- 日志格式 `[ALLM_{tag}]: [{HH:mm:ss}] {message}` 不变
- `XuaLogger.Common.*` 委托方式不变

---

## 四、实施步骤（4 阶段，按序执行）

### 阶段 1：新建文件，零影响

**操作**：创建全部 13 个新文件，不修改或删除任何现有文件。

**验收标准**：
1. 项目编译通过（新文件有编译错误但旧功能通过预处理器排除不影响）
2. 新文件的类/方法签名与规格完全一致
3. `PromptManager.Build()` 的提示词加载逻辑与 `TranslatorTask.Init` 中的对应段落逐行一致
4. `AutoLLMConfig.FromInitializationContext()` 的所有配置读取 key 与原始代码一致
5. `TaskQueue.DequeueBatch()` 的选择算法与原始 `SelectTasks()` 一致（用单元测试验证）
6. `LlmClient.Translate()` 的请求构造与原始 `LlmClient.Translate()` 一致

**该阶段结束时**：13 个新文件存在，旧文件未被改动。

---

### 阶段 2：切换端点，验证功能

**操作**：
1. 修改 `.csproj`：移除旧文件引用，添加新文件引用
2. 修改 `Logger.Init` 签名（删除 INI 解析，改为接收 `AutoLLMConfig`）
3. 在 `AutoLLMConfig.FromInitializationContext()` 中实现日志等级解析
4. 删除旧文件：`AutoLLMTranslatorEndpoint.cs`、`TranslatorTask.cs`
5. 重命名 `Config.cs` → `Prompt.cs`，`Config.PromptBase` → `Prompt.Default`

**验收标准**（需在 Unity 游戏中运行）：
1. BepInEx 加载插件成功，日志出现 `"AutoLLM Translate"` 端点
2. 发送单条短文本 → LLM 返回翻译 → 游戏内文本被替换
3. 连续发送 5 条短文本 → 合并为 1 批次 → 翻译成功（日志显示合并发送）
4. 发送超长文本（>MaxWordCount）→ 单独 1 批次 → 翻译成功
5. API 返回 429 → 出现 `"限速退避"` 日志 → 等待后自动重试 → 翻译成功
6. API 返回 500 → 出现 `"重试"` 日志 → 重试直到成功或耗尽
7. 重试耗尽 → 出现 `"重试耗尽"` 日志 → 游戏中原文本保持不变（翻译失败但不崩溃）
8. 插件关闭时无异常

**该阶段结束时**：旧架构完全替换，所有功能正常。

---

### 阶段 3：清理 SimpleJson

**操作**：
1. 删除 `SerializeTexts` 和 `ParseTexts` 方法及其唯一相关的逻辑
2. 确认无未使用字段/方法残留在 SimpleJson 中

**验收标准**：
1. 项目编译通过
2. Grep 确认 `SerializeTexts` 和 `ParseTexts` 无任何引用
3. 现有翻译功能不受影响（SimpleJson 的其他方法未变）

---

### 阶段 4：终验收与文档

**操作**：
1. 运行完整回归测试（游戏内 30 分钟翻译压力测试）
2. 验证 README 中的配置参数全部生效
3. 确认 `Logger.cs` 删除 INI 解析器后日志输出不变

**验收标准**：
1. 所有配置参数（Model/URL/APIKey/MaxWordCount/ParallelCount/MaxRetry/MaxContext/ModelParams/CustomPrompt/HalfWidth）均生效
2. Token 统计日志格式与原来一致
3. 对话历史在多批次间正确维持（ParallelCount=1 时）
4. HalfWidth 转换正确
5. CustomPrompt 文件加载/创建逻辑正确

---

## 五、验证用例（最小必须集）

以下测试无需 Unity 即可运行，应作为阶段 1 的一部分实现：

### 5.1 `BatchSelectionTests`（TaskQueue.DequeueBatch）

| # | 输入 | 预期 |
|---|------|------|
| 1 | 3 条短文本（各 10 字符），maxChars=100 | 1 批 3 条 |
| 2 | 3 条文本（各 50 字符），maxChars=80 | 第 1 批 1 条（50 字符），第 2 批 1 条（50 字符），第 3 批 1 条（50 字符）— 因为 50+50=100>80，每次只取一条 |
| 3 | 混入 retryCount=1 和 retryCount=0 的任务 | 不混搭：retryCount=0 的任务单独一批，retryCount=1 的单独一批 |
| 4 | retryCount=3 的任务 + 正常任务 | retryCount=3 的任务单独成批，正常任务另批 |
| 5 | 队列满（2000 条）时 TryEnqueue | 返回 false，任务未入队 |
| 6 | 空队列 DequeueBatch | 返回空列表 |

### 5.2 `ConversationHistoryTests`

| # | 输入 | 预期 |
|---|------|------|
| 1 | Enabled=false, AppendExchange | 历史保持为空 |
| 2 | MaxContext=100, system(50字)+user(50字)+历史(200字) | 估算 tokens=150 > 100 → 历史清空 |
| 3 | 追加 3 轮对话 | TurnCount=3, BuildMessages 包含 3 user + 3 assistant |
| 4 | AppendExchange 后 CheckAndClearIfOverLimit 未超限 | 历史不清空，_cachedHistoryChars 正确累加 |

### 5.3 `RateLimitGuardTests`

| # | 输入 | 预期 |
|---|------|------|
| 1 | 首次 OnRateLimited | CurrentDelayMs=5000, IsBlocked=true |
| 2 | 连续 3 次 OnRateLimited | CurrentDelayMs=20000 (5→10→20s) |
| 3 | OnRateLimited 后 Reset | IsBlocked=false, CurrentDelayMs=0 |
| 4 | 超出最大值后继续 OnRateLimited | CurrentDelayMs 不超过 60000 |

### 5.4 `RetryHandlerTests`

| # | 输入 | 预期 |
|---|------|------|
| 1 | retryCount=0, maxRetry=10, ShouldRetry | true |
| 2 | retryCount=10, maxRetry=10, ShouldRetry | false（已达到上限） |
| 3 | IncrementRetry 后 retryCount | 原值+1 |

### 5.5 `SimpleJsonTests`（已有逻辑，补充回归）

| # | 输入 | 预期 |
|---|------|------|
| 1 | Serialize(Dictionary) | 合法 JSON 字符串 |
| 2 | ParseJsonObject(合法JSON) | 正确 Dictionary |
| 3 | ParseJsonObject(非法JSON) | 返回空 Dictionary，不抛异常 |
| 4 | ParseSseChunk(SSE数据) | 正确提取 content 和 usage |

### 5.6 `PromptManagerTests`

| # | 输入 | 预期 |
|---|------|------|
| 1 | CustomPrompt=false | 返回 Prompt.Default 且已替换 {{SOURCE_LAN}}/{{TARGET_LAN}} |
| 2 | CustomPrompt=true, 文件存在 | 返回文件内容（已替换占位符） |
| 3 | CustomPrompt=true, 文件不存在 | 创建文件（内容=Prompt.Default）→ 返回默认提示词 |
| 4 | CustomPrompt=true, 文件读取异常 | 返回 Prompt.Default（降级） |

---

## 六、删除清单

| 文件 | 原因 |
|------|------|
| `AutoLLMTranslatorEndpoint.cs` | 旧 WwwEndpoint 实现，被直接 `ITranslateEndpoint` 实现替代 |
| `TranslatorTask.cs` | 663 行单体的职责已拆分到 8 个文件 |
| `Config.cs` | 重命名为 `Prompt.cs` |
| `SimpleJson.SerializeTexts()` | 仅 HTTP 代理层使用，代理层已移除 |
| `SimpleJson.ParseTexts()` | 仅 HTTP 代理层使用，代理层已移除 |
| `Logger.Init(string bepinExRoot)` 中的 `ParseIniFile/ContainsLevel/GetBoolValue` | INI 解析移入 `AutoLLMConfig.FromInitializationContext` |
| `TranslatorTask._listener` `HttpListener` 全部基础设施 | HTTP 代理层已移除 |
| `TaskData.TryRespond()` HTTP 响应序列化 | 不再需要 HTTP 响应 |
| `ProcessRequest()` | HTTP 请求处理，代理层已移除 |

---

## 七、风险与缓解措施

| 风险 | 概率 | 影响 | 缓解 |
|------|------|------|------|
| `ITranslateEndpoint.Translate()` 协程模式与框架调度器的交互存在未知差异 | 中 | 翻译卡死或重复翻译 | 阶段 2 在真实 Unity 游戏中测试，对比翻译前后文本 |
| 标准错误重试触发限速退避或反之（错误分类错误） | 低 | 不必要重试或忽略限速 | 在 ProcessBatch 中严格判断：仅 429 → isRateLimit=true |
| 翻译请求长时间无响应导致 ThreadPool 线程耗尽 | 低 | 翻译卡死，新任务无法处理 | HttpWebRequest.Timeout=600000（10分钟）作为兜底；ParallelCount 默认 1 限制并发 |
| 旧 Logger.cs 删除 INI 解析后，BepInEx 日志等级配置不再生效 | 中 | 调试日志过多或过少 | 阶段 1 验证 `AutoLLMConfig.FromInitializationContext` 中的日志等级解析与原来一致 |
| 全角转半角正则行为变化 | 低 | 翻译结果中残留全角标点 | 单元测试覆盖 HalfWidthRegex 的典型输入 |

---

## 八、不变项确认

以下设计特性在重构中**完全不改变**其运行时行为：

1. SSE 流式解析：逐行读取 → `data:` 前缀判断 → `[DONE]` 检测 → 内容累积 → usage 提取（最后一条 SSE chunk 的 usage 覆盖之前的）
2. Token 估算公式：`(systemPrompt.Length + inputJson.Length + cachedHistoryChars) / 2`
3. 批量合并规则：不混搭重试/非重试、retryCount>2 单独成批、字数限制且至少保 1 条
4. 限速退避公式：首次 5000ms，每次翻倍，上限 60000ms
5. 请求体覆盖顺序：extraParams 先设置 → model/messages/response_format/stream/stream_options 覆盖
6. 非限速错误清空退避：`_rateLimitDelayMs = 0`
7. 全角转半角映射：`c - 0xFEE0`
8. 对话历史在 ParallelCount>1 时禁用
9. URL 自动补尾：`/v1` → `/v1/chat/completions`
10. APIKey 为空时不发送 Authorization 头
