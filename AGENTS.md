# AGENTS.md — XUnity.AutoLLMTranslator 项目指引

本文档为 OpenCode 与此仓库协作时提供完整上下文。

> ## 架构变更说明（双线程 + 关历史，最新为准）
> 本仓库已从"单线程翻译 + 借对话历史清空驱动术语合并"改为**双线程架构**：
> - **翻译线程**（`TranslationOrchestrator.WorkerLoop`）：系统提示词内嵌当前术语表，只产出译文 `{"1":"译文"}`，不再产出 glossary。**对话历史已禁用**（`ConversationHistory.Enabled=false`），`RecordExchange`/`RecordApiUsage`/`CheckAndClearIfOverLimit`/`OnHistoryCleared` 全部停用/移除；`_history` 仅保留系统提示词基线与 `AllocKeys`/`EstimateTokens`。
> - **术语抽取线程**（新增 `Translation/GlossaryWorker.cs`）：翻译线程每批派发后通过 `EnqueueSources` 投递本批原文；独立调用 LLM 抽取 `{"glossary":{...}}`，附带**本地最近原文环形缓冲**（非 LLM 历史）供跨句判断；`AddPendingTerms` 即时落盘 + 按 `GlossaryMergeThreshold` 阈值 `MergePending` 注入 `_glossary`（单点驱动，不再借历史清空事件）。合并后下一批翻译线程从 `_glossary` 重建系统提示词即时生效。
> - **`RateLimitGuard`** 已加锁，两个线程共享同一退避状态（任一撞 429 双方共同退避）。
> - **PromptManager** 占位符统一中文：`{{源语言}}` `{{目标语言}}` `{{术语表}}` `{{最近原文}}`；提示词模板拆为 `TranslationWithGlossary`（翻译）与 `GlossaryExtractionOnly`（术语抽取），`BuildGlossaryPrompt` 拆为 `BuildTranslationPrompt` 与 `BuildGlossaryExtractionPrompt`；自定义提示词文件改为三节 `[普通模式提示词]`/`[翻译模式提示词]`/`[术语抽取模式提示词]`；现存无分节标题的单文件（整文件即普通提示词）首次加载自动重写为三节 INI 格式；旧英文占位符加载时自动迁移为中文。
> - 配置新增：`GlossaryMergeThreshold`(3) / `GlossaryContextLines`(50) / `GlossaryBatchMerge`(true)，`CachedGlossaryPrompt` → `CachedTranslationPrompt` + `CachedExtractionPrompt`。
> - 下方历史章节（尤其第二章流程图、第六章 `ProcessBatch` 步骤、第十一/十二章 GlossaryManager/ConversationHistory 行为）仅作背景参考，以本说明为准。

---

## 一、项目概述

XUnity.AutoLLMTranslator 是一个 **Unity 游戏文本自动翻译插件**，基于 [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) 框架开发。

1. 将游戏文本通过 LLM API（兼容 OpenAI 格式）进行翻译
2. 支持 SSE 流式解析、对话历史（缓存复用）、批量合并、并发控制、限速指数退避
3. 目标框架 .NET Framework 3.5 (net35)，兼容 Unity Mono 运行时
4. 零 NuGet 依赖
5. 本地 DLL 引用存放于 `packages/` 目录

**分支说明**：`dev` 分支已完成架构重构（消除 HTTP 代理层），比 `main` 分支代码更简洁、职责更清晰。

---

## 二、目录结构与文件职责

```
XUnity.AutoLLMTranslator/
├── Endpoint/
│   └── AutoLLMTranslateEndpoint.cs   # 框架适配层：实现 ITranslateEndpoint，协程等待
├── Configuration/
│   ├── AutoLLMConfig.cs              # 配置读取、验证、预处理
│   └── PromptManager.cs              # 系统提示词构建 + 内建默认提示词常量（Default/Glossary）；单文件 AutoLLM_CustomPrompt.txt 用 INI 风格分节标题分两节
├── Orchestration/
│   ├── TranslationOrchestrator.cs    # 核心调度引擎：工作线程、批次处理、术语表集成（每批即时落盘 + 历史清空注入）
│   ├── BatchResponseParser.cs        # LLM 响应解析 → 译文分发到批内任务（含全角转半角）
│   ├── TaskQueue.cs                  # 线程安全任务队列（AutoResetEvent 信号）
│   └── Guards.cs                     # RetryHandler（重试策略）+ RateLimitGuard（指数退避限速，5s→10s→…→60s）
├── Translation/
│   ├── LlmClient.cs                  # LLM API 客户端接口 ILlmClient + 实现：HttpWebRequest + SSE 流解析
│   ├── ConversationHistory.cs        # 对话历史管理（线程安全，chars×0.75 估算 + API 精确追踪）
│   └── GlossaryManager.cs            # 自动术语表管理（文件读写 + 新术语每批即时落盘 + 历史清空时注入系统提示词）
├── Models.cs                         # 数据模型：LlmMessage, LlmResult, LlmUsage, TaskState, TranslationTask
├── SimpleJson.cs                     # 零依赖 JSON 序列化/解析器
├── Logger.cs                         # 日志封装（委托给 XuaLogger.AutoTranslator + [AutoLLM] 标签）
├── XUnity.AutoLLMTranslator.csproj   # SDK 风格项目文件
├── packages/                         # XUnity 框架本地 DLL 引用
├── .github/workflows/release.yml     # CI：Windows 构建 + GitHub Release 自动发布
├── README.md / README.en.md          # 中英双语项目说明
└── LICENSE.txt                       # 许可证
```

> 全部类型均为 `internal`、全局命名空间（无 `namespace` 块）；目录仅用于组织，不映射到 CLR 命名空间。SDK 风格 csproj 自动 glob 所有 `.cs`，文件移动无需改工程。

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
  │  ConversationHistory.AllocKeys() → 为批内各任务分配 "1"/"2"/... 编号（单调递增，历史清空时重置回 1）
  │  构建 {"1":"原文1","2":"原文2"} JSON（键值对应，编号跨批次唯一避免与历史重号）
  │  ConversationHistory.BuildMessages() → [system, history, user]
  │  LlmClient.Translate() → SSE 流式请求 LLM API
  │  BatchResponseParser.ParseAndDispatch() → 按 UserKey 取回译文 + 全角半角 + 分发译文到各 TranslationTask
  │  成功 → ConversationHistory.RecordApiUsage() + RecordExchange()
  │  失败(429) → RateLimitGuard 指数退避，ReEnqueue（不耗重试次数）
  │  失败(其他) → RetryHandler 判断是否重试
  ▼
Endpoint 协程轮询 task.IsCompleted → context.Complete(translated)
```

---

## 四、各文件详解

### 1. `Endpoint/AutoLLMTranslateEndpoint.cs`（91 行）

| 要点 | 说明 |
|---|---|
| 接口 | 实现 `ITranslateEndpoint`（dev 分支新架构，不再通过 HTTP 代理） |
| `Id` | `"AutoLLMTranslate"`，框架通过此 ID 绑定端点 |
| `MaxTranslationsPerRequest=1` | 框架每次传 1 条，内部批量合并 |
| `MaxConcurrency=500` | 高标记，实际并发固定为 1（`ParallelCount` 已废弃） |
| `Initialize()` | 读取配置 → 验证 → 创建 `TranslationOrchestrator` → `Start()` |
| `Translate()` | 协程方法：创建 `TranslationTask` → 入队 → `yield return null` 轮询完成 |
| `Dispose()` | 调用 `_orchestrator.Shutdown()` |

### 2. `Configuration/AutoLLMConfig.cs`（234 行）

| 要点 | 说明 |
|---|---|
| 配置来源 | `IInitializationContext.GetOrCreateSetting("AutoLLM", key, default)` |
| 配置项 | Model, URL, APIKey, MaxRetry(5), MaxContext(4096), ModelParams, CustomPrompt(false), AutoGlossary(false), HalfWidth(true), DisableSpamChecks(true) |
| URL 补全 | 保留用户填写的 `Url` 原值用于日志；结尾为 `/v1` → 派生 `EndpointUrl` 追加 `/chat/completions`；结尾为 `/v1/` → 追加 `chat/completions`；其余 `EndpointUrl=Url` |
| 验证 | Model 或 URL 缺失 → 抛 `EndpointInitializationException`（由框架统一捕获并标记端点初始化失败），与其它端点（Yandex/Watson/Custom …）一致 |
| 系统提示词 | 在 `AutoLLMConfig.FromInitializationContext` 末尾通过 `PromptManager.Build()` 预构建并缓存到 `CachedSystemPrompt`；AutoGlossary 时额外 `BuildGlossaryPrompt()` 存入 `CachedGlossaryPrompt` |
| 日志等级 | 不本地门控；统一经 `XuaLogger.AutoTranslator` 转发，由 BepInEx 的 Console/Disk listener 按 `BepInEx.cfg` 的 `[Logging.Console]`/`[Logging.Disk].LogLevels` 过滤 |
| BepInEx 定位 | 从 TranslatorDirectory 向上查找（含 `core/` 子目录 或 目录名为 `BepInEx`） |
| `ParallelCount` | `public const int = 1`，已废弃不再读取配置；多处语义（并发、对话历史启停、术语表落盘路径）均依赖此固定值 |

### 3. `Configuration/PromptManager.cs`

内建默认提示词常量（`Default`、`Glossary`，均为 `private const`）+ 自定义提示词文件加载逻辑。

| 要点 | 说明 |
|---|---|
| `Build(config)` | 根据 `CustomPrompt` 决定使用内建默认还是从单一文件 `AutoLLM_CustomPrompt.txt` 读取【普通模式分节】 |
| `BuildGlossaryPrompt(config)` | 构建术语表模式提示词，从同一文件读取【术语表模式分节】，设置 `config.GlossaryPath`，保留 `{{GLOSSARY}}` 占位符 |
| 自定义文件路径 | `{BepInExRoot}/config/AutoLLM_CustomPrompt.txt`（单一文件，用 INI 风格分节标题分两节） |
| 文件分节标题 | 常量 `PromptManager.DefaultSectionHeader`（`[普通模式提示词]`）、`TranslationSectionHeader`（`[翻译模式提示词]`）、`ExtractionSectionHeader`（`[术语抽取模式提示词]`）：标题行之下到下一个标题之前为对应分节（参见顶部"架构变更说明"） |
| 占位符替换 | `{{SOURCE_LAN}}` → 源语言，`{{TARGET_LAN}}` → 目标语言；`{{GLOSSARY}}` 由 GlossaryManager 运行时填充 |
| 首次开启 | 自动创建含两套内建默认提示词（用 `DefaultSectionHeader` / `GlossarySectionHeader` 分节）的模板文件，方便用户修改 |
| `LoadPromptSection()` | 通用加载逻辑：customPrompt=false 用默认，true 时按 `wantGlossary` 选取对应分节（按 INI 风格分节标题切分；检测到无分节标题的最旧版单节文件会自动重写为新版 INI 分节格式） |

### 4. `Models.cs`（72 行）

合并数据模型，全部 `internal` 全局命名空间。

| 类型 | 字段/说明 |
|---|---|
| `LlmMessage` | `Role`（system/user/assistant），`Content` |
| `LlmResult` | `FullResponse`, `Usage`(LlmUsage), `ChunkCount`, `DoneReceived`, `ElapsedMs` |
| `LlmUsage` | `PromptTokens`, `CompletionTokens`, `CacheHitTokens`, `CacheMissTokens` |
| `enum TaskState` | `Waiting, Processing, Completed, Failed` |
| `TranslationTask` | `UntranslatedText`, `TranslatedText?`, `ErrorMessage?`, `State`, `RetryCount`, `CharLen`, `CreatedTick`，`volatile bool IsCompleted`（Endpoint 协程轮询字段），便利方法 `MarkCompleted/MarkFailed/ResetForRetry` |

### 5. `Orchestration/TranslationOrchestrator.cs`

**核心调度引擎**，管理整个翻译生命周期。

| 成员 | 职责 |
|---|---|
| `WorkerLoop()` | 后台线程主循环：WaitOne(50ms) → 限速检查 → 并发检查 → 积压告警 → DispatchBatches |
| `DispatchBatches()` | 循环取批直到并发满或队列空，标记 Processing，提交 ThreadPool；历史清空时触发 `OnHistoryCleared` |
| `ProcessBatch()` | 批次翻译核心流程（见下）；响应解析/分发/全角半角交由 `BatchResponseParser` |
| `SelectBatch()` | Token 感知选批：超限走「下一句截断」/「单条丢弃」/「清空历史后纳入」三条分支 |
| `BuildInputJson()` | 构建 `{"1":"原文1","2":"原文2"}` 格式 JSON（编号键由 `ConversationHistory.AllocKeys` 单调递增分配，跨批次唯一避免与历史重号；历史清空时重置回 1） |
| `OnHistoryCleared()` | 历史清空后将暂存术语注入 `_glossary`（`MergePending`，仅内存合并）+ 更新系统提示词 token 估算（仅 AutoGlossary） |
| Token 统计 | `_totalInputTokens`, `_totalOutputTokens`, `_totalCacheHitTokens`, `_totalCacheMissTokens`（`Interlocked.Add` 累加） |

**ProcessBatch 流程**：
1. 收集文本，构建输入 JSON
2. 构建系统提示词：AutoGlossary 时用 `GlossaryManager.BuildSystemPrompt()`（含术语表），否则用 `CachedSystemPrompt`
3. `_history.BuildMessages()` 组装 [system, ...history, user]
4. `_llmClient.Translate()` 同步阻塞调用（net35 限制）
5. 成功：`_rateLimitGuard.Reset()` → `BatchResponseParser.ParseAndDispatch()` 解析+分发+全角半角 → `AddPendingTerms` 收集新术语并即时全量落盘（每批有新术语即写文件，防止意外停止丢失；新术语暂不进系统提示词） → `RecordApiUsage` + `RecordExchange`
6. 失败(429)：`_rateLimitGuard.OnRateLimited()`，`ReEnqueue`（不消耗重试次数）
7. 失败(其他)：`_retryHandler.ShouldRetry()` → IncrementRetry → ReEnqueue，超限则 MarkFailed

### 6. `Orchestration/BatchResponseParser.cs`

纯转换组件：LLM JSON 响应 → 批内任务分发，不接触队列/历史/重试/限速状态。

| 要点 | 说明 |
|---|---|
| `HalfWidthRegex` | 静态编译正则，全角符号 `[！-～]` 转半角（偏移 `0xFEE0`）；从 Orchestrator 迁入 |
| `ParseAndDispatch(result, batch, config, out glossaryObj)` | 校验非空 → `ParseJsonObject` 解析顶层对象 → 按各任务 `UserKey` 直接从对象中取译文（普通模式/术语表模式同为平铺结构 `{"1":"译文1","2":"译文2"[,"glossary":{...}]}`） → 术语表模式额外取顶层 `glossary` 对象 → 按需全角转半角 → `MarkCompleted` 分发；返回完成数，`out` 暴露本轮新术语 |
| 文本超限/单条丢弃 | 仍由 Orchestrator 的 `SelectBatch` 负责，本类只处理已成功返回的响应 |

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

### 8. `Orchestration/Guards.cs`

合并两个协作件，均为 `internal` 全局命名空间，仅 Orchestrator 持有。

**`RateLimitGuard`**：

| 要点 | 说明 |
|---|---|
| 退避策略 | 初次 5000ms → 翻倍 → 上限 60000ms（即 5s→10s→20s→40s→60s） |
| `OnRateLimited()` | 首次 5s，后续 `delay*2`（上限 60s） |
| `Reset()` | 收到正常响应后清零 |
| `IsBlocked()` | 基于 `Environment.TickCount` 判断冷却期是否结束 |

**`RetryHandler`**：

| 要点 | 说明 |
|---|---|
| 构造参数 | `maxRetry`（来自配置，默认 5） |
| `ShouldRetry(task)` | `task.RetryCount < _maxRetry` |
| `IncrementRetry(task)` | `task.RetryCount++` |

### 9. `Translation/LlmClient.cs`（158 行，含 `ILlmClient` 接口）

接口与实现位于同一文件顶部。

**`ILlmClient`**：接口抽象，方便测试时替换 LLM 客户端实现。

```csharp
LlmResult Translate(string url, string apiKey, string model,
    List<LlmMessage> messages, Dictionary<string, object> extraParams);
```

**`LlmClient`**：

| 要点 | 说明 |
|---|---|
| 协议 | HTTP POST，Bearer 认证，Content-Type: application/json |
| 超时 | Timeout=600000(10min)，ReadWriteTimeout=120000(2min) |
| 请求体 | 合并 extraParams → 设置 model, messages, response_format(json_object), stream(true), stream_options({include_usage:true}) |
| SSE 解析 | `data:` 前缀行（兼容有无空格）→ 逐 chunk 调 `SimpleJson.ParseSseChunk` 同时提取 content + usage（单次解析） |
| `CacheStatsSupported` | 静态属性：首次响应后检测 `prompt_cache_hit_tokens/miss_tokens` 字段 |
| 消息序列化 | `LlmMessage` → `Dictionary<string, object>`（强类型模型，SimpleJson 不支持反射序列化） |
| 未收到 [DONE] | 发出警告但保留已拼接的响应内容 |

### 10. `Translation/ConversationHistory.cs`

| 要点 | 说明 |
|---|---|
| 线程安全 | `lock(_lock)` 保护所有读写 |
| 开关 | `Enabled` 属性，ParallelCount 已废弃固定为 1，对话历史始终启用 |
| Token 估算 | `chars × 0.75`（整数运算）；API 返回 usage 后切换为精确模式 |
| 超限清空 | `CheckAndClearIfOverLimit()` 返回 bool 表示是否触发清空（供 Orchestrator 触发术语表合并） |
| `ClearHistory()` | 主动清空，返回 bool（始终 true） |
| `UpdateSystemPrompt()` | 术语表合并后系统提示词变长时更新基线 token 估算（清空历史） |
| 消息格式 | `List<LlmMessage>` 强类型模型 |
| `RecordExchange()` | 追加 user+assistant 一轮对话；精确模式仅追加消息，回退模式累加估算 token |
| `RecordApiUsage()` | 记录 API 返回的精确 token 统计并切换模式 |
| `EstimateTokens()` | 纯文本 token 估算（chars × 3/4） |
| `IncrementDiscardCount()` | 单条超限丢弃计数 |

### 11. `Translation/GlossaryManager.cs`

自动术语表管理器，仅在 `AutoGlossary=true` 时启用。

| 要点 | 说明 |
|---|---|
| 文件路径 | `{BepInExRoot}/config/AutoLLM_Glossary.json`（JSON 格式 `{"原文":"译文"}`） |
| 线程安全 | `lock(_lock)` 保护 `_glossary` 与 `_pendingNew` |
| `RenderForPrompt()` | 渲染术语表为提示词文本（每行 `原文 => 译文`），无术语返回 `（无）`；仅渲染已注入的 `_glossary`，不含暂存 `_pendingNew` |
| `BuildSystemPrompt()` | 将术语表内容填入模板的 `{{GLOSSARY}}` 占位符，返回完整系统提示词 |
| `AddPendingTerms()` | 收集模型响应中的 glossary 到内存缓冲 `_pendingNew`，并立即全量落盘 `_glossary + _pendingNew`（每批有新术语即写文件，防止意外丢失）；新术语暂不进 `_glossary`，故不进系统提示词 |
| `MergePending()` | 历史清空时调用：将暂存新术语注入 `_glossary`（从而进入系统提示词）；文件已由每批即时落盘，此处仅做内存合并，返回新增条目数 |
| 落盘时机 | 由 Orchestrator 调 `AddPendingTerms()` 每批即时落盘（防游戏意外停止丢失）；`MergePending()` 仅在对话历史清空时（`OnHistoryCleared`）做内存合并注入 `_glossary`，不再写文件 |
| 首次运行 | 自动创建空 `{}` 文件 |

### 12. `SimpleJson.cs`（258 行）

| 要点 | 说明 |
|---|---|
| 序列化 | `Serialize(object)` 支持 null/bool/string/数值/IDictionary/IEnumerable（禁止匿名类型，无反射分支） |
| 解析 | 完整的递归下降解析器：`ParseObject` / `ParseArray` / `ParseValue` / `ReadString` / `ReadNumber` |
| SSE 专用 | `ParseSseChunk(json, out content, out usage)` 单次解析同时提取 content 和 usage（避免双解析） |
| 特殊方法 | `ParseJsonObject()` / `ParseModelParams()` 返回 `Dictionary<string, object>` |
| Unicode | 支持 `\uXXXX` 转义序列 |
| 容错 | 解析失败返回空 dict/list，不抛异常 |

### 13. `Logger.cs`（约 40 行）

| 要点 | 说明 |
|---|---|
| 底层 | 委托给 `XuaLogger.AutoTranslator`（与框架自带翻译器 GoogleTranslate/DeepL/Bing 一致），无需直接引用 BepInEx，BepInEx 5/6 双版本通吃 |
| 格式 | 消息统一加注 `[AutoLLM] ` 前缀（最终显示形如 `[INFO][XUnity.AutoTranslator]: [AutoLLM] 消息`） |
| 等级 | 不本地门控；Info/Warn/Debug 全部无条件转发，由 BepInEx 的 Console/Disk listener 按 `BepInEx.cfg` 的 `LogLevels` 过滤。Error 始终输出，且失败时兜底到 `Console.Error` |
| 初始化 | 无 `Init`；不再从 `AutoLLMConfig` 同步日志开关状态 |

---

## 五、重要设计约定

### 数据与序列化

1. **禁止匿名类型**：所有传递给 `SimpleJson.Serialize()` 的对象必须是 `Dictionary<string, object>`、`List<Dictionary>` 或基元类型。`Serialize()` 遇到非 IDictionary/IEnumerable/基元的对象会静默返回 `"obj.ToString()"` 字符串而非抛异常——调用方需自行确保类型正确
2. **消息使用强类型**：`LlmMessage` 模型（定义于 `Models.cs`）替代原始 `Dictionary<string, object>` 直传方式
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
2. `ParallelCount > 1` 路径已废弃：`ConversationHistory.Enabled` 固定为 true
3. `DisableSpamChecks` 默认 true（减少误关）
4. `HalfWidth` 默认 true（全角符号转半角）
5. 日志等级不本地控制，统一经 `XuaLogger.AutoTranslator` 转发，由 BepInEx 的 listener 按 `BepInEx.cfg` 过滤
6. `MaxContext` 双重角色：控制对话历史上限 + 单批次 token 上限
7. `AutoGlossary` 与 `CustomPrompt` 独立：CustomPrompt 控制单一自定义提示词文件 `AutoLLM_CustomPrompt.txt`（用 INI 风格分节标题分两节），AutoGlossary 控制从该文件读取哪一节（普通/术语表）；两套开关独立

### 术语表约定

1. **术语表文件**：`{BepInExRoot}/config/AutoLLM_Glossary.json`，JSON 格式 `{"原文":"译文"}`
2. **术语表位置**：作为系统提示词的一部分（拼接到 `{{GLOSSARY}}` 占位符后），不作为独立 system 消息——保持缓存前缀稳定
3. **更新时机**：每批有新术语即落盘（`AddPendingTerms`，防止游戏意外停止丢失）；但仅对话历史清空时（`CheckAndClearIfOverLimit` 或 `ClearHistory`）才由 `MergePending` 注入 `_glossary`，保证一轮对话上下文内系统提示词稳定
4. **新术语缓冲**：`BatchResponseParser.ParseAndDispatch` 解析响应 glossary 字段并通过 `out` 返回，由 Orchestrator 调 `AddPendingTerms` 存入内存 `_pendingNew` 并即时全量落盘（`_glossary + _pendingNew` 合并视图），新术语暂不进 `_glossary`，故暂不进系统提示词
5. **输入/输出键值对应**：输入为 `{"1":"原文1","2":"原文2",...}`，输出为 `{"1":"译文1","2":"译文2",...}`（普通模式）；`AutoGlossary=true` 时再加 `"glossary":{...}`。编号由 `ConversationHistory.AllocKeys` 在同一对话窗口内单调递增、跨批次唯一避免与历史同号条目混淆；历史清空时（`CheckAndClearIfOverLimit` / `ClearHistory` / `UpdateSystemPrompt`）一并重置回 1
6. **Token 估算**：术语表合并后系统提示词变长，`ConversationHistory.UpdateSystemPrompt()` 重置基线 token

### 错误处理

1. HTTP 429 → 指数退避（5s→10s→20s→40s→60s），不消耗重试次数
2. 其他错误 → 重试计数递增，超限后标记 Failed
3. 批次部分解析成功 → 已解析的完成，未解析的重试（与原始行为一致）
4. 完整解析成功 → 追加到对话历史（只有完整成功才更新历史）

---

## 六、构建与开发

### 前置条件

1. .NET 8.0+ SDK（用于构建 net35 目标）
2. 本地 `packages/` 目录包含 XUnity 框架 DLL

### 构建命令

```bash
# 构建输出到 bin/Release/net35/XUnity.AutoLLMTranslator.dll
# 仓库无 .sln，直接对 csproj 构建
dotnet build XUnity.AutoLLMTranslator.csproj -c Release
```

### 构建流程

1. `dotnet build` 编译为 `net35` 目标
2. 直接引用 `packages/` 中的 XUnity DLL（不打包合并，运行时由 BepInEx 加载同目录 DLL）

> 历史版本曾通过 ILRepack 合并 + XCOPY 复制到游戏目录，当前 SDK 风格 csproj 已移除该流程。

### 发布流程（GitHub Actions）

- 推送到任意分支 → 构建 + 上传 Artifact（`XUnity.AutoLLMTranslator`）
- 推送 `v*` 标签 → 额外创建/更新 GitHub Release（tag 名为版本号，已存在则追加 assets）

### 测试

**本项目无测试代码**。验证方式：
1. 本地 `dotnet build` 编译通过
2. 放入 Unity 游戏（BepInEx 环境）实际测试翻译流程

---

## 七、目标框架限制（net35 须知）

1. **无 `async/await`**：HTTP 调用使用同步 `HttpWebRequest`，在 ThreadPool 线程上阻塞执行
2. **无 `System.Text.Json`**：使用自研 `SimpleJson`（258 行递归下降解析器）
3. **无 `HttpClient`**：使用 `HttpWebRequest` / `HttpWebResponse`
4. **`StringBuilder` 无 `AppendJoin`**：手动循环拼接
5. **LINQ 有限**：`List<T>` 的 `ForEach` 不存在，使用 `foreach`
6. **`Path.Combine` 仅支持 2 参数**：多层路径需嵌套调用

---

## 八、OpenCode 协作指引

### 修改代码时注意

1. 阅读相关文件后再修改，理解全局设计约束（目标框架、序列化限制、线程模型）
2. 保持文件职责单一：Endpoint → Orchestration → Translation 三层清晰
3. 新增依赖需评估 net35 兼容性
4. 序列化只使用 `SimpleJson` 或 `Dictionary<string, object>`
5. 日志通过 `Logger` 静态方法输出，不直接使用 `XuaLogger`

### 常见任务位置

| 任务 | 文件 |
|---|---|
| 调整翻译批次逻辑 | `Orchestration/TranslationOrchestrator.cs` |
| 调整 LLM 响应解析/译文分发/全角半角 | `Orchestration/BatchResponseParser.cs` |
| 修改 LLM 请求格式 | `Translation/LlmClient.cs`（接口 `ILlmClient` 与实现 `LlmClient` 同文件） |
| 修改内建默认系统提示词 | `Configuration/PromptManager.cs`（常量 `Default`/`Glossary` 已合入此类） |
| 添加配置项 | `Configuration/AutoLLMConfig.cs` |
| 调整重试/限速策略 | `Orchestration/Guards.cs`（`RetryHandler` + `RateLimitGuard`） |
| 修改 JSON 解析 | `SimpleJson.cs` |
| 修改对话历史策略 | `Translation/ConversationHistory.cs` |
| 修改自动术语表 | `Translation/GlossaryManager.cs`（术语表逻辑）+ `Configuration/PromptManager.cs`（术语表模式提示词常量 `Glossary`） |
| 修改端点框架适配 | `Endpoint/AutoLLMTranslateEndpoint.cs` |

### 禁止事项

1. 不引入外部 JSON 库（如 Newtonsoft.Json）
2. 不使用 `async/await`（net35 不支持）
3. 不使用结构化日志框架
4. 不修改 `packages/` 中的 DLL 文件
