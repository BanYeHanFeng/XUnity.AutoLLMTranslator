# CLAUDE.md — XUnity.AutoLLMTranslator 项目说明

本文档为 Claude Code 与此仓库协作时提供指引。

---

## 一、项目概述

XUnity.AutoLLMTranslator 是一个 **Unity 游戏文本自动翻译插件**，基于 [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) 框架开发。它将游戏中的文本通过 LLM API（兼容 OpenAI 格式）进行翻译，支持流式 SSE 解析、对话历史、批处理、并发控制、限速退避等特性。

**目标框架**: .NET Framework 3.5 (net35)，兼容 Unity Mono 运行时  
**依赖**: 本地 DLL（`packages/` 目录），非 NuGet 包

---

## 二、文件结构与职责

### 1. `AutoLLMTranslatorEndpoint.cs` — 框架适配层（52 行）

**作用**: 作为 XUnity.AutoTranslator 框架的端点实现，桥接框架与内部翻译引擎。

| 成员 | 说明 |
|---|---|
| `class LLMTranslatorEndpoint : WwwEndpoint` | 继承自 `WwwEndpoint`，注册为 `Endpoint=AutoLLMTranslate` |
| `MaxTranslationsPerRequest = 1` | 每次只处理一条文本（批处理在内部完成，这里不能改） |
| `MaxConcurrency = 500` | 高并发标记，实际并发由内部 `ParallelCount` 控制 |
| `Initialize()` | 设置翻译延迟 0.1s，初始化 `TranslatorTask` |
| `OnCreateRequest()` | 拦截框架翻译请求 → 序列化为 `{"texts":[...]}` → POST 到内部 HTTP 服务器 (`127.0.0.1:port`) |
| `OnExtractTranslation()` | 从内部服务器响应中解析翻译结果 → 返回给框架 |

**关键设计**: 框架每次只传 1 条文本，但内部 `TranslatorTask` 通过 HTTP 接口累积多条后批量发送给 LLM，以充分利用上下文窗口。

---

### 2. `TranslatorTask.cs` — 核心调度引擎（541 行）

**作用**: 项目的核心，负责 HTTP 服务、任务队列、批量调度、结果映射、重试逻辑与统计追踪。

| 类/方法 | 说明 |
|---|---|
| `class TaskData` | 内部任务实体，包含 `texts`（原文数组）、`result`（译文数组）、`retryCount`、`state`（Waiting/Processing/Completed/Failed/Closed）以及 `TryRespond()` 方法 |
| `Init()` | 从框架配置读取所有参数，初始化 HTTP Listener（端口 20000+ 自动递增），启动监听线程和轮询线程 |
| `ProcessRequest()` | HTTP 请求处理：POST 解析 `{"texts":[...]}` → 调用 `AddTask`；GET 返回运行状态 |
| `AddTask()` | 将文本加入任务队列（加锁线程安全） |
| `SelectTasks()` | 从队列中选取一批待处理任务：优先选取等待中任务，重试次数 >2 的批次优先发送，累计字符达 `MaxWordCount` 即触发 |
| `ProcessTaskBatch()` | **核心翻译流程**：<br>① 构建输入 JSON `{"1":"原文1","2":"原文2"}` <br>② 组装 system prompt + 对话历史 + 当前输入 <br>③ 调用 `LlmClient.Translate()` 发送请求 <br>④ 解析 LLM 返回的 JSON 结果 `{"1":"译文1","2":"译文2"}` <br>⑤ 全角→半角转换 <br>⑥ 结果回填到各个 TaskData 并响应客户端 <br>⑦ 成功批次追加到对话历史 <br>⑧ HTTP 429 → 指数退避（不消耗重试次数）<br>⑨ 其他失败 → 递增重试计数，超限则标记失败 |
| `Polling()` | 轮询线程（50ms 间隔）：<br>① 检查当前处理数是否达上限<br>② 统计等待任务数<br>③ 调用 `SelectTasks()` 分批<br>④ 通过 `ThreadPool.QueueUserWorkItem` 提交执行 |
| `Shutdown()` | 优雅关闭：停止监听器 |

**关键设计**:
- `HalfWidthRegex` — 静态编译正则，用于全角符号转半角
- 累计统计 `_totalInputTokens` / `_totalOutputTokens` / `_totalCacheHitTokens` / `_totalCacheMissTokens`
- `_rateLimitDelayMs` 指数退避：5s → 10s → 20s → 40s → 60s（上限）
- 任务积压 >200 时发出警告

---

### 3. `LlmClient.cs` — LLM API 客户端（131 行）

**作用**: 无状态的 HTTP 客户端，封装了向 LLM API 发送请求、解析 SSE 流式响应、提取用量统计的完整逻辑。

| 成员 | 说明 |
|---|---|
| `class Result` | 翻译结果数据类：`FullResponse`、`PromptTokens`、`CompletionTokens`、`CacheHitTokens`、`CacheMissTokens`、`ChunkCount`、`DoneReceived`、`ElapsedMs` |
| `Translate()` | 核心方法：<br>① 合并 `ModelParams` 到请求体<br>② 设 `model`、`messages`、`response_format: json_object`、`stream: true`、`stream_options: {include_usage: true}`<br>③ POST 请求，Bearer 认证 <br>④ 流式读取 SSE（`data: ` 前缀行）<br>⑤ 逐个 chunk 提取 `choices[0].delta.content` 增量拼接<br>⑥ 提取 `usage` 对象（含 prompt_tokens、completion_tokens、cache 统计）<br>⑦ 缓存 cache_stats 能力检测（首次返回后确定是否支持）|
| `CacheStatsSupported` | 静态属性，标记 API 是否支持缓存命中统计 |

**关键设计**:
- 超时设置：`Timeout=600000`(10min)，`ReadWriteTimeout=120000`(2min) — 适配 DeepSeek 等长思考模型
- 未收到 `[DONE]` 标记时发出警告但保留已有结果
- `_warnedUsageMissing` 避免重复警告

---

### 4. `ConversationHistory.cs` — 对话历史管理（59 行）

**作用**: 管理多轮翻译的对话上下文，实现 LLM 缓存复用，提升翻译一致性。

| 成员 | 说明 |
|---|---|
| `Enabled` | 是否启用历史（`ParallelCount > 1` 时自动禁用，防止交错） |
| `MaxContext` | 最大上下文限额（token 数），超出自动清空 |
| `TurnCount` | 当前历史轮数 |
| `BuildMessages()` | 构建完整消息列表：`[system_prompt, ...历史消息, user_input]` |
| `AppendExchange()` | 将成功的一轮翻译（user 输入 + assistant 输出）追加到历史 |
| `CheckAndClearIfOverLimit()` | 估算 token 数（字符数/2），若超限则清空历史并记录清空次数 |

**关键设计**:
- 线程安全：所有操作通过 `lock(_lock)` 保护
- 历史格式：`Dictionary<string, object>` 而非匿名类型，避免 SimpleJson 反射序列化问题

---

### 5. `Config.cs` — 系统提示词模板（17 行）

**作用**: 存放翻译专用的 system prompt 模板。

包含三个占位符：
| 占位符 | 替换内容 |
|---|---|
| `{{SOURCE_LAN}}` | 源语言（由框架提供） |
| `{{TARGET_LAN}}` | 目标语言（由框架提供） |
| `{{EXTRA_PROMPT}}` | 用户自定义附加提示（ExtraPrompt 配置项） |

提示词规则要点：
1. 不得拒绝翻译
2. 分析语境、统一术语
3. 保留格式标签（`%s`、`[TAG]`、`<label>`、HTML 标签等）
4. 不添加解释说明
5. 输出纯 JSON 格式，键与输入一一对应

---

### 6. `SimpleJson.cs` — 轻量 JSON 序列化/解析器（306 行）

**作用**: 零依赖的 JSON 工具类，替代 Newtonsoft.Json，适配 .NET 3.5 环境。

#### 序列化方法

| 方法 | 说明 |
|---|---|
| `Serialize(object)` | 通用序列化：支持 null/bool/string/数值/IDictionary/IEnumerable/反射对象 |
| `SerializeTexts(string[])` | 包装为 `{"texts": [...]}` |
| `EscapeString(string)` | JSON 字符串转义（`"`、`\`、`\n`、`\r`、`\t`、控制字符 `\u00xx`） |

#### 解析方法

| 方法 | 说明 |
|---|---|
| `ParseTexts(string)` | 解析 `{"texts": [...]}` 返回 `string[]` |
| `ParseSseContent(string)` | 从 SSE chunk 中提取 `choices[0].delta.content` |
| `ParseSseUsage(string)` | 从 SSE chunk 中提取 `usage` 对象 |
| `ParseModelParams(string)` | 解析 JSON 为 `Dictionary<string, object>`（用于 ModelParams 配置） |
| `ParseJsonObject(string)` | 通用 JSON 对象解析 |
| `ParseObject()` / `ParseArray()` / `ParseValue()` / `ReadString()` / `ReadNumber()` | 递归下降解析器内部方法 |

**关键设计**:
- 支持 `\uXXXX` Unicode 转义序列
- 数字解析区分 int/long/double/float
- 所有解析方法 try-catch，失败返回空值而不是抛异常

---

### 7. `Logger.cs` — 日志封装（134 行）

**作用**: 封装 XUnity 的 `XuaLogger.Common`，提供等级控制的日志输出。

| 成员 | 说明 |
|---|---|
| `Init(bepinExRoot)` | 读取 `{BepInEx根目录}/config/BepInEx.cfg` 中的日志配置 |
| `ParseIniFile()` | 简易 INI 解析器，提取各 section 的键值对 |
| `ContainsLevel()` | 检查逗号分隔的 LogLevels 中是否含指定等级（含 `All`） |
| `Info()` / `Debug()` / `Warn()` / `Error()` | 四种日志级别，格式：`[ALLM_标签]: [HH:mm:ss] 消息` |

**日志等级控制**:
- Error 始终启用
- Info / Warn / Debug 由 `BepInEx.cfg` 中的 `[Logging.Console]` 和 `[Logging.Disk]` 共同控制（任一开启即生效）
- 日志实际输出到 `LogOutput.log`（XUnity 统一管理）

**关键设计**:
- 从 `TranslatorDirectory` 向上遍历最多 10 层查找 BepInEx 根目录（含 `core/` 子目录或目录名为 `BepInEx`）

---

### 8. `XUnity.AutoLLMTranslator.csproj` — 项目文件（38 行）

| 配置项 | 值 | 说明 |
|---|---|---|
| `TargetFramework` | `net35` | 兼容 Unity Mono 运行时 |
| `ImplicitUsings` | `disable` | 所有 `using` 需显式声明 |
| `Nullable` | `enable` | 启用可空引用类型 |
| `LangVersion` | `latest` | 使用最新 C# 语法（需确保兼容 net35） |

**依赖项**:
- `XUnity.AutoTranslator.Plugin.Core.dll` (本地)
- `XUnity.AutoTranslator.Plugin.ExtProtocol.dll` (本地)
- `XUnity.Common.dll` (本地)
- `ILRepack.MSBuild.Task` (NuGet，仅构建工具)

**构建流程**:
1. `dotnet build` 编译
2. **ILRepack**（本地构建时）：将引用的 DLL 合并到输出程序集
3. **XCOPY**（本地构建时）：复制到 `$(GameDir)` 配置的游戏目录
4. CI 环境（`CI=true`）：跳过 ILRepack 和 XCOPY

---

### 9. 其余文件

| 文件 | 说明 |
|---|---|
| `Properties/launchSettings.json` | 启动配置（Visual Studio 调试用） |
| `README.md` | 项目 README，包含配置说明和相较于上游的改动列表 |
| `LICENSE.txt` | 许可证文件 |
| `.gitignore` | Git 忽略规则 |
| `.github/` | GitHub Actions CI 工作流（Windows + .NET 8.x SDK，tag 触发 release） |
| `.gitattributes` | Git 属性配置 |
| `packages/` | 存放 XUnity 框架的本地 DLL 依赖 |

---

## 三、整体工作流程

```
游戏文本
  │
  ▼
XUnity.AutoTranslator 框架
  │  OnCreateRequest() → POST {"texts":["原文"]}
  ▼
HttpListener (127.0.0.1:20000+)
  │  AddTask() → 加入任务队列
  ▼
Polling() 线程 (50ms 轮询)
  │  SelectTasks() → 按 MaxWordCount 分批
  │  ThreadPool.QueueUserWorkItem()
  ▼
ProcessTaskBatch()
  │  BuildInputJson() → {"1":"原文1","2":"原文2"}
  │  ConversationHistory.BuildMessages() → [system, history, user]
  │  LlmClient.Translate() → SSE 流式请求 LLM API
  │  解析 JSON 结果 → 回填译文到 TaskData
  │  成功 → AppendExchange() 更新对话历史
  │  失败(429) → 指数退避，不耗重试次数
  │  失败(其他) → retryCount++，最多 MaxRetry 次
  ▼
TaskData.TryRespond() → 返回译文给框架
  │  OnExtractTranslation() → 译文回传给游戏
  ▼
游戏显示译文
```

---

## 四、配置参数详解

| 参数 | 默认值 | 说明 |
|---|---|---|
| `Model` | (必填) | 模型名称，需支持 JSON Output 模式 |
| `URL` | (必填) | API 地址，自动补全 `/v1/chat/completions` |
| `APIKey` | (必填) | API 密钥，留空则不发送 Authorization 头 |
| `MaxWordCount` | 2500 | 批处理最大字符数，达到即触发翻译 |
| `ParallelCount` | 1 | 并发请求数，>1 时自动禁用对话历史 |
| `MaxContext` | 1024 | 上下文限额(token)，超出清空历史。建议 ≤50000 |
| `MaxRetry` | 10 | 翻译失败最大重试次数 |
| `ModelParams` | (空) | 额外 JSON 参数，合并到请求体（如 `{"temperature":0.3}`） |
| `ExtraPrompt` | (空) | 附加提示词，追加到 system prompt 末尾 |
| `HalfWidth` | True | 是否将全角符号转为半角 |
| `DisableSpamChecks` | True | 是否禁用 XUnity 的 spam 检测 |

---

## 五、重要约定

- ❌ **无测试代码**
- ✅ 消息使用 `Dictionary<string, object>` 而非匿名类型（避免 SimpleJson 反射）
- ✅ `HalfWidthRegex` 为 `static readonly` 编译正则
- ✅ `ParallelCount > 1` 时自动禁用对话历史（防止并发交错）
- ✅ 缓存命中统计仅 DeepSeek 等部分 API 在流式响应中返回
- ✅ `.csproj` 使用 SDK 风格，自动包含所有 `*.cs` 文件
- ✅ SSE 流要求模型设置 `stream_options: {include_usage: true}` 才能获取 token 统计
