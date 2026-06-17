# AGENTS.md — XUnity.AutoLLMTranslator 项目指引

本文档为 OpenCode 与此仓库协作时提供完整上下文。

---

## 一、项目概述

XUnity.AutoLLMTranslator 是一个 **Unity 游戏文本自动翻译插件**，基于 [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) 框架开发。

1. 将游戏文本通过 LLM API（兼容 OpenAI 格式）进行翻译
2. 支持 SSE 流式解析、对话历史（缓存复用）、批量合并、并发控制、限速指数退避
3. 双目标框架：`net35`（Mono / BepInEx 5）+ `net6.0`（IL2CPP / BepInEx 6），共用同一份源码
4. 零 NuGet 依赖
5. 本地 DLL 引用按目标框架分目录存放于 `packages/net35/` 与 `packages/net6.0/`

**分支说明**：`dev` 分支已完成架构重构（消除 HTTP 代理层），比 `main` 分支代码更简洁、职责更清晰。

---

## 二、目录结构与文件职责

```
XUnity.AutoLLMTranslator/
├── Endpoint/
│   └── AutoLLMTranslateEndpoint.cs   # 框架适配层：实现 ITranslateEndpoint，协程等待
├── Configuration/
│   ├── AutoLLMConfig.cs              # 配置读取、验证、预处理（225行）
│   └── PromptManager.cs              # 系统提示词构建（默认/自定义 .txt）
├── Models/
│   ├── LlmModels.cs                  # 数据模型：LlmMessage, LlmResult, LlmUsage
│   └── TranslationTask.cs            # 翻译任务实体 + 状态机
├── Orchestration/
│   ├── TranslationOrchestrator.cs    # 核心调度引擎：工作线程、批次处理（431行）
│   ├── TaskQueue.cs                  # 线程安全任务队列（AutoResetEvent 信号）
│   ├── RateLimitGuard.cs             # 指数退避限速控制（5s→10s→20s→40s→60s）
│   └── RetryHandler.cs               # 重试策略（最多 MaxRetry 次）
├── Translation/
│   ├── ILlmClient.cs                 # LLM 客户端接口（便于测试替换）
│   ├── LlmClient.cs                  # LLM API 客户端：HttpWebRequest + SSE 流解析
│   └── ConversationHistory.cs        # 对话历史管理（线程安全，chars×0.75 估算 + API 精确追踪）
├── Prompt.cs                         # 默认系统提示词常量
├── SimpleJson.cs                     # 零依赖 JSON 序列化/解析器
├── Logger.cs                         # 日志封装（委托给 XuaLogger.Common）
├── XUnity.AutoLLMTranslator.csproj   # SDK 风格项目文件
├── packages/
│   ├── net35/                       # net35 框架 DLL（Mono / BepInEx 5）
│   └── net6.0/                      # net6.0 框架 DLL（IL2CPP / BepInEx 6）
├── .github/workflows/release.yml     # CI：Windows 构建 + GitHub Release 自动发布
├── README.md / README.en.md          # 中英双语项目说明
└── LICENSE.txt                       # 许可证
```

---

## 三、整体工作流程

```
游戏文本
  │
  ▼
XUnity.AutoTranslator 框架 (ITranslateEndpoint)
  │  Translate() 协程 → 创建 TranslationTask → TaskQueue.TryEnqueue()
  ▼
TaskQueue (List<TranslationTask> + AutoResetEvent _signal)
  │  _signal.Set() 唤醒工作线程
  ▼
WorkerLoop (后台线程, IsBackground=true)
  │  WaitOne(50ms) → 限速检查 → 并发检查
  │  DispatchBatches() → DequeueAll() → Token 感知选择
  ▼
ProcessBatch() (ThreadPool 线程)
  │  构建 {"1":"原文1","2":"原文2"} JSON
  │  ConversationHistory.BuildMessages() → [system, history, user]
  │  LlmClient.Translate() → SSE 流式请求 LLM API
  │  解析 JSON 结果 → 分发译文到各 TranslationTask
  │  成功 → ConversationHistory.RecordApiUsage() + RecordExchange()
  │  失败(429) → RateLimitGuard 指数退避，ReEnqueue（不耗重试次数）
  │  失败(其他) → RetryHandler 判断是否重试
  ▼
Endpoint 协程轮询 task.IsCompleted → context.Complete(translated)
```

---

## 四、各文件详解

### 1. `Endpoint/AutoLLMTranslateEndpoint.cs`（93 行）

| 要点 | 说明 |
|---|---|
| 接口 | 实现 `ITranslateEndpoint`（dev 分支新架构，不再通过 HTTP 代理） |
| `Id` | `"AutoLLMTranslate"`，框架通过此 ID 绑定端点 |
| `MaxTranslationsPerRequest=1` | 框架每次传 1 条，内部批量合并 |
| `MaxConcurrency=500` | 高标记，实际并发由 `ParallelCount` 控制 |
| `Initialize()` | 读取配置 → 验证 → 创建 `TranslationOrchestrator` → `Start()` |
| `Translate()` | 协程方法：创建 `TranslationTask` → 入队 → `yield return null` 轮询完成 |
| `Dispose()` | 调用 `_orchestrator.Shutdown()` |

### 2. `Configuration/AutoLLMConfig.cs`（225 行）

| 要点 | 说明 |
|---|---|
| 配置来源 | `IInitializationContext.GetOrCreateSetting("AutoLLM", key, default)` |
| 配置项 | Model, URL, APIKey, ParallelCount(1), MaxRetry(5), MaxContext(4096), ModelParams, CustomPrompt, HalfWidth(true), DisableSpamChecks(true) |
| URL 补全 | `/v1` → `/v1/chat/completions`，`/v1/` → `/v1/chat/completions` |
| ThreadPool | 确保最小线程数 ≥ ParallelCount+2 |
| 系统提示词 | 初始化时通过 `PromptManager.Build()` 预构建并缓存到 `CachedSystemPrompt` |
| 日志等级 | 从 `BepInEx/config/BepInEx.cfg` 的 `[Logging.Console]` 和 `[Logging.Disk]` 联合读取 |
| BepInEx 定位 | 从 TranslatorDirectory 向上查找（含 `core/` 子目录 或 目录名为 `BepInEx`） |

### 3. `Configuration/PromptManager.cs`（63 行）

| 要点 | 说明 |
|---|---|
| `Build(config)` | 根据 `CustomPrompt` 决定使用默认提示词还是读取 `.txt` 文件 |
| 自定义文件路径 | `{BepInExRoot}/config/AutoLLM_CustomPrompt.txt` |
| 占位符替换 | `{{SOURCE_LAN}}` → 源语言，`{{TARGET_LAN}}` → 目标语言 |
| 首次开启 | 自动创建默认模板文件，方便用户修改 |

### 4. `Models/LlmModels.cs`（25 行）

| 类型 | 字段 |
|---|---|
| `LlmMessage` | `Role`（system/user/assistant），`Content` |
| `LlmResult` | `FullResponse`, `Usage`(LlmUsage), `ChunkCount`, `DoneReceived`, `ElapsedMs` |
| `LlmUsage` | `PromptTokens`, `CompletionTokens`, `CacheHitTokens`, `CacheMissTokens` |

### 5. `Models/TranslationTask.cs`（47 行）

| 要点 | 说明 |
|---|---|
| 状态枚举 | `TaskState { Waiting, Processing, Completed, Failed }` |
| 关键字段 | `UntranslatedText`, `TranslatedText`, `ErrorMessage`, `State`, `RetryCount`, `CharLen`, `CreatedTick` |
| `volatile bool IsCompleted` | Endpoint 协程轮询此字段判断任务完成 |
| `MarkCompleted()` | 设置译文 + 状态 = Completed + IsCompleted = true |
| `MarkFailed()` | 设置错误信息 + 状态 = Failed + IsCompleted = true |
| `ResetForRetry()` | 状态重置为 Waiting，清空结果（用于重试入队） |

### 6. `Orchestration/TranslationOrchestrator.cs`（431 行）

**核心调度引擎**，管理整个翻译生命周期。

| 成员 | 职责 |
|---|---|
| `WorkerLoop()` | 后台线程主循环：WaitOne(50ms) → 限速检查 → 并发检查 → 积压告警 → DispatchBatches |
| `DispatchBatches()` | 循环取批直到并发满或队列空，标记 Processing，提交 ThreadPool |
| `ProcessBatch()` | 批次翻译核心流程（见下） |
| `BuildInputJson()` | 构建 `{"1":"原文1","2":"原文2"}` 格式 JSON |
| `HalfWidthRegex` | 静态编译正则，全角符号 `[！-～]` 转半角（偏移 `0xFEE0`） |
| Token 统计 | `_totalInputTokens`, `_totalOutputTokens`, `_totalCacheHitTokens`, `_totalCacheMissTokens` |

**ProcessBatch 流程**：
1. 收集文本，构建输入 JSON
2. `_history.BuildMessages()` 组装 [system, ...history, user]
3. `_llmClient.Translate()` 同步阻塞调用（net35 限制）
4. 成功：`_rateLimitGuard.Reset()`，解析 JSON 结果，全角转半角，分发译文，`RecordApiUsage` + `RecordExchange`
5. 失败(429)：`_rateLimitGuard.OnRateLimited()`，`ReEnqueue`（不消耗重试次数）
6. 失败(其他)：`_retryHandler.ShouldRetry()` → IncrementRetry → ReEnqueue，超限则 MarkFailed

### 7. `Orchestration/TaskQueue.cs`（119 行）

| 要点 | 说明 |
|---|---|
| 底层 | `List<TranslationTask>` + head 指针环形缓冲区 + `AutoResetEvent` + `lock` 保护 |
| 容量 | 上限 2000（`_outstandingCount` 控制） |
| `TryEnqueue()` | 满时返回 false，否则入队并 `_signal.Set()` |
| `DequeueAll()` | 批量取队首任务：不混搭重试/非重试，retryCount>2 单独成批；无字符数上限 |
| `ReEnqueueFront()` | 将溢出任务插入队首，保持顺序，优先于新到达任务处理 |
| `ReEnqueue()` | 重试用：`ResetForRetry()` 后队尾入队，不增 `_outstandingCount` |
| `MarkCompleted()` | `Interlocked.Decrement` 递减计数 |
| `CompactIfNeeded()` | 当 head 超过容量一半时自动压缩，避免内存泄漏 |

### 8. `Orchestration/RateLimitGuard.cs`（37 行）

| 要点 | 说明 |
|---|---|
| 退避策略 | 初次 5000ms → 翻倍 → 上限 60000ms |
| `OnRateLimited()` | 首次 5s，后续 `delay*2`（上限 60s） |
| `Reset()` | 收到正常响应后清零 |
| `IsBlocked()` | 基于 `Environment.TickCount` 判断冷却期是否结束 |

### 9. `Orchestration/RetryHandler.cs`（24 行）

| 要点 | 说明 |
|---|---|
| 构造参数 | `maxRetry`（来自配置，默认 10） |
| `ShouldRetry(task)` | `task.RetryCount < _maxRetry` |
| `IncrementRetry(task)` | `task.RetryCount++` |

### 10. `Translation/ILlmClient.cs`（19 行）

接口抽象，方便测试时替换 LLM 客户端实现。

```csharp
LlmResult Translate(string url, string apiKey, string model,
    List<LlmMessage> messages, Dictionary<string, object> extraParams);
```

### 11. `Translation/LlmClient.cs`（140 行）

| 要点 | 说明 |
|---|---|
| 协议 | HTTP POST，Bearer 认证，Content-Type: application/json |
| 超时 | Timeout=600000(10min)，ReadWriteTimeout=120000(2min) |
| 请求体 | 合并 extraParams → 设置 model, messages, response_format(json_object), stream(true), stream_options({include_usage:true}) |
| SSE 解析 | `data:` 前缀行（兼容有无空格）→ 逐 chunk 提取 choices[0].delta.content → 最后一次提取 usage 对象 |
| `CacheStatsSupported` | 静态属性：首次响应后检测 `prompt_cache_hit_tokens/miss_tokens` 字段 |
| 消息序列化 | `LlmMessage` → `Dictionary<string, object>`（强类型模型，SimpleJson 不支持反射序列化） |
| 未收到 [DONE] | 发出警告但保留已拼接的响应内容 |

### 12. `Translation/ConversationHistory.cs`（151 行）

| 要点 | 说明 |
|---|---|
| 线程安全 | `lock(_lock)` 保护所有读写 |
| 开关 | `Enabled` 属性，`ParallelCount > 1` 时自动禁用（防止并发交错） |
| Token 估算 | `chars × 0.75`（整数运算）；API 返回 usage 后切换为精确模式 |
| 超限清空 | `CheckAndClearIfOverLimit()` 在每次 DispatchBatches 中调用，超限则清空历史并重置 |
| 消息格式 | `List<LlmMessage>` 强类型模型 |
| `RecordExchange()` | 追加 user+assistant 一轮对话；精确模式仅追加消息，回退模式累加估算 token |
| `RecordApiUsage()` | 记录 API 返回的精确 token 统计并切换模式 |
| `EstimateTokens()` | 纯文本 token 估算（chars × 3/4） |
| `IncrementDiscardCount()` | 单条超限丢弃计数 |

### 13. `Prompt.cs`（14 行）

| 要点 | 说明 |
|---|---|
| 常量 | `Prompt.Default`，含 `{{SOURCE_LAN}}` 和 `{{TARGET_LAN}}` 占位符 |
| 规则 | 不得拒绝翻译、分析语境统一术语、保留格式标签、输出纯 JSON、不添加解释 |

### 14. `SimpleJson.cs`（258 行）

| 要点 | 说明 |
|---|---|
| 序列化 | `Serialize(object)` 支持 null/bool/string/数值/IDictionary/IEnumerable（禁止匿名类型，无反射分支） |
| 解析 | 完整的递归下降解析器：`ParseObject` / `ParseArray` / `ParseValue` / `ReadString` / `ReadNumber` |
| SSE 专用 | `ParseSseChunk()` 单次解析同时提取 content 和 usage（避免双解析） |
| 特殊方法 | `ParseJsonObject()` / `ParseModelParams()` 返回 `Dictionary<string, object>` |
| Unicode | 支持 `\uXXXX` 转义序列 |
| 容错 | 解析失败返回空 dict/list，不抛异常 |

### 15. `Logger.cs`（63 行）

| 要点 | 说明 |
|---|---|
| 底层 | 委托给 `XuaLogger.Common`（XUnity 框架日志） |
| 格式 | `[ALLM_标签]: [HH:mm:ss] 消息` |
| 等级 | Info/Warn/Debug 由配置控制开关，Error 始终输出 |
| 初始化 | `Logger.Init(config)` 从 AutoLLMConfig 同步日志开关状态 |

---

## 五、重要设计约定

### 数据与序列化

1. **禁止匿名类型**：所有传递给 `SimpleJson.Serialize()` 的对象必须是 `Dictionary<string, object>`、`List<Dictionary>` 或基元类型。`Serialize()` 遇到非 IDictionary/IEnumerable/基元的对象会静默返回 `"obj.ToString()"` 字符串而非抛异常——调用方需自行确保类型正确
2. **消息使用强类型**：dev 分支引入 `LlmMessage` 模型，替代原始 `Dictionary<string, object>` 直传方式
3. **SSE 单次解析**：`SimpleJson.ParseSseChunk()` 一次调用同时提取 content 和 usage，避免重复遍历 JSON

### 可空引用类型（NRT）

项目启用 `<Nullable>enable</Nullable>`，接受编译器对 nullability 的静态检查：

1. **可能为 null 的引用类型用 `?` 标注**：如 `string?`（`TranslationTask.TranslatedText`）、`LlmUsage?`（`LlmResult.Usage`）、`Thread?`（`_workerThread`）
2. **语义非空但编译器无法验证初始化的 auto-property 用初始化器**：`= ""`（如 `LlmMessage.Role`）或 `= null!`（如 `AutoLLMConfig.CachedSystemPrompt`，IsValid=true 时由 `PromptManager.Build` 设置）
3. **XUnity 框架成员视为 null-oblivious**：框架 DLL 无 NRT 标注，`context.UntranslatedText` 等属性在 `string.IsNullOrEmpty` 检查后用 `!` 断言非空
4. **`SimpleJson.ParseValue` 返回 `object`（非空）**：JSON null 用 `null!` 标注，约定 `Dictionary<string, object>` 的 value 非空

### 并发与线程安全

1. **TaskQueue**：`lock(_lock)` + `AutoResetEvent`（事件驱动 + 50ms 保底轮询）
2. **ConversationHistory**：`lock(_lock)` 保护所有方法
3. **`_processingCount`**：`volatile` 修饰，主循环读，ProcessBatch 的 finally 中递减
4. **`TranslationTask.IsCompleted`**：`volatile`，由 endpoint 协程（Unity 主线程）轮询
5. **`_shutdownRequested`**：`volatile`，Shutdown 设置后工作线程退出循环

### 批处理规则（TaskQueue.DequeueAll）

1. 不混搭：重试任务（RetryCount>0）与非重试任务不同批
2. 优先发送高重试次数：retryCount>2 的任务立即形成独立批次
3. 顺序保证：FIFO，Peek+Dequeue 原子操作

### 配置约定

1. URL 自动补 `/v1/chat/completions`
2. `ParallelCount > 1` 时 `ConversationHistory.Enabled = false`
3. `DisableSpamChecks` 默认 true（减少误关）
4. `HalfWidth` 默认 true（全角符号转半角）
5. 日志等级从 BepInEx 配置读取，非独立控制
6. `MaxContext` 双重角色：控制对话历史上限 + 单批次 token 上限

### 错误处理

1. HTTP 429 → 指数退避（5s→10s→20s→40s→60s），不消耗重试次数
2. 其他错误 → 重试计数递增，超限后标记 Failed
3. 批次部分解析成功 → 已解析的完成，未解析的重试（与原始行为一致）
4. 完整解析成功 → 追加到对话历史（只有完整成功才更新历史）

---

## 六、构建与开发

### 前置条件

1. .NET 8.0+ SDK（同时构建 net35 与 net6.0 两个目标；net35 引用程序集通过 `Microsoft.NETFramework.ReferenceAssemblies` NuGet 包自动获取）
2. 本地 `packages/net35/` 与 `packages/net6.0/` 目录包含对应目标框架的 XUnity 框架 DLL
   - `packages/net35/*.dll`：从 XUnity.AutoTranslator 框架的 net35 构建产物获取
   - `packages/net6.0/*.dll`：从 XUnity.AutoTranslator 框架的 net6.0（IL2CPP）构建产物获取

### 更新 packages/ 中的 XUnity 框架 DLL（维护说明）

当 XUnity.AutoTranslator 框架版本升级时，需重新构建并更新 `packages/` 中的 3 个核心 DLL：
`XUnity.AutoTranslator.Plugin.Core.dll`、`XUnity.AutoTranslator.Plugin.ExtProtocol.dll`、`XUnity.Common.dll`。

**Windows 环境**（推荐，框架 PostBuild 脚本原生支持）：
```powershell
cd <XUnity.AutoTranslator 仓库根目录>
dotnet build src\XUnity.AutoTranslator.Plugin.Core\XUnity.AutoTranslator.Plugin.Core.csproj -c Release
# 产物路径：
#   src\XUnity.AutoTranslator.Plugin.Core\bin\Release\net35\*.dll   → 拷贝到 packages\net35\
#   src\XUnity.AutoTranslator.Plugin.Core\bin\Release\net6.0\*.dll  → 拷贝到 packages\net6.0\
```

**Linux/非 Windows 环境**：框架的 PostBuild Target 使用 Windows cmd 语法（`if $(ConfigurationName) == Release (...)` + `XCOPY`），在 Linux 上会构建失败。需在框架根目录临时放置 `Directory.Build.targets` 覆盖 PostBuild 为 no-op：
```xml
<Project>
  <Target Name="PostBuild" />
  <Target Name="PostBuildNET35" />
  <Target Name="PostBuildNET460" />
  <Target Name="PostBuildNET472" />
  <Target Name="ILRepackNET35" />
  <Target Name="ILRepackNET460" />
</Project>
```
此外，框架 `src/XUnity.AutoTranslator.Plugin.Core/Properties/Resources.resx` 引用的 `translations/statictranslations.txt` 文件未入 git，需创建一个占位空文件（本仓库不使用该静态翻译表，空文件无副作用）。构建完成后删除这两个临时文件。

### 构建命令

```bash
# 同时构建 net35 与 net6.0 两个目标
# 产物：bin/Release/net35/XUnity.AutoLLMTranslator.dll       (Mono)
#       bin/Release/net6.0/XUnity.AutoLLMTranslator.dll      (IL2CPP)
dotnet build XUnity.AutoLLMTranslator.csproj -c Release

# 仅构建单个目标
dotnet build XUnity.AutoLLMTranslator.csproj -c Release -f net35
dotnet build XUnity.AutoLLMTranslator.csproj -c Release -f net6.0
```

### 构建流程

1. `dotnet build` 同时编译 `net35` 与 `net6.0` 两个目标
2. 通过 `Choose`/`When` 结构按 `$(TargetFramework)` 选择对应 `packages/` 子目录的 DLL
3. Reference 设置 `<Private>false</Private>`，不复制到输出目录（运行时由 BepInEx 框架提供同目录 DLL）
4. net6.0 目标抑制 `SYSLIB0014` 警告（HttpWebRequest 在 .NET 6 obsolete，但本仓库与框架保持一致不改用 HttpClient）
5. 调整 `AssemblySearchPaths` 将 `{HintPathFromItem}` 置于 `{CandidateAssemblyFiles}` 之前。SDK 默认顺序中 CandidateAssemblyFiles 会扫描项目目录下所有 DLL（含 `packages/net35/`），在双目标构建时可能先匹配到错误版本并锁定版本号，导致 HintPath 指向的正确版本被跳过。此调整为双目标共用同名 DLL 引用的必需配置

> 历史版本曾通过 ILRepack 合并 + XCOPY 复制到游戏目录，当前 SDK 风格 csproj 已移除该流程。

### 发布流程（GitHub Actions）

- 推送到任意分支 → 构建 + 上传两个 Artifact：
  - `XUnity.AutoLLMTranslator-Mono`（net35）
  - `XUnity.AutoLLMTranslator-IL2CPP`（net6.0，重命名为 `XUnity.AutoLLMTranslator.IL2CPP.dll`）
- 推送 `v*` 标签 → 额外创建/更新 GitHub Release（同时上传 Mono 与 IL2CPP 两个 DLL）

### 测试

**本项目无测试代码**。验证方式：
1. 本地 `dotnet build` 双目标编译通过（零警告零错误）
2. 放入 Unity 游戏（BepInEx 5 Mono 或 BepInEx 6 IL2CPP 环境）实际测试翻译流程

---

## 七、目标框架限制（net35 须知，net6.0 共用同一份源码）

以下限制源自 net35，但因双目标共用源码，net6.0 编译时同样遵循：

1. **无 `async/await`**：HTTP 调用使用同步 `HttpWebRequest`，在 ThreadPool 线程上阻塞执行
2. **无 `System.Text.Json`**：使用自研 `SimpleJson`（258 行递归下降解析器）
3. **无 `HttpClient`**：使用 `HttpWebRequest` / `HttpWebResponse`（net6.0 下 obsolete 但可用，与框架 `XUnityWebClient` 一致，csproj 抑制 `SYSLIB0014`）
4. **`StringBuilder` 无 `AppendJoin`**：手动循环拼接
5. **LINQ 有限**：`List<T>` 的 `ForEach` 不存在，使用 `foreach`
6. **`Path.Combine` 仅支持 2 参数**：多层路径需嵌套调用
7. **可空引用类型（NRT）双目标兼容**：net6.0 严格可空检查下，对 `Dictionary.TryGetValue(out object)` 等用 `out var` 推断；`object.ToString()` 显式 `?? ""` 兜底

---

## 八、OpenCode 协作指引

### 修改代码时注意

1. 阅读相关文件后再修改，理解全局设计约束（目标框架、序列化限制、线程模型）
2. 保持文件职责单一：Endpoint → Orchestration → Translation 三层清晰
3. 新增依赖需评估 net35 与 net6.0 双目标兼容性
4. 序列化只使用 `SimpleJson` 或 `Dictionary<string, object>`
5. 日志通过 `Logger` 静态方法输出，不直接使用 `XuaLogger`

### 常见任务位置

| 任务 | 文件 |
|---|---|
| 调整翻译批次逻辑 | `Orchestration/TranslationOrchestrator.cs` |
| 修改 LLM 请求格式 | `Translation/LlmClient.cs` |
| 修改系统提示词 | `Prompt.cs` 或 `Configuration/PromptManager.cs` |
| 添加配置项 | `Configuration/AutoLLMConfig.cs` |
| 调整重试/限速策略 | `Orchestration/RetryHandler.cs` / `Orchestration/RateLimitGuard.cs` |
| 修改 JSON 解析 | `SimpleJson.cs` |
| 修改对话历史策略 | `Translation/ConversationHistory.cs` |
| 修改端点框架适配 | `Endpoint/AutoLLMTranslateEndpoint.cs` |

### 禁止事项

1. 不引入外部 JSON 库（如 Newtonsoft.Json）
2. 不使用 `async/await`（net35 不支持）
3. 不使用结构化日志框架
4. 不修改 `packages/net35/` 与 `packages/net6.0/` 中的 DLL 文件
5. 不引入仅在 net6.0 可用而 net35 缺失的 API（如 `Span<T>`、`async/await`、`HttpClient`），保持双目标源码共用
