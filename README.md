## 简介

基于上游 NothingNullNull/XUnity.AutoLLMTranslator 根据自己需求二改的
我本人并不懂代码，所有代码都用ai编程，可能会出现意料之外的bug

具体使用方法请看上游仓库(https://github.com/NothingNullNull/XUnity.AutoLLMTranslator)

## 致谢

```
XUnity.AutoTranslator 该插件基于此项目开发
NothingNullNull/XUnity.AutoLLMTranslator 上游仓库
```

## 主要优化

### 翻译质量与格式
- JSON Output 模式（`response_format: {"type": "json_object"}`），输出结构化 JSON，解析更稳定
- 连续对话历史替代历史翻译/模糊搜索，保留翻译轮次作为上下文，自动触发 DeepSeek KV 缓存（缓存命中 0.02元 vs 未命中 1元/百万token）
- 移除硬编码的 temperature/max_tokens 参数，由 API 和 ModelParams 控制
- History 配置（0=禁用，-1=无限制，正数=N 轮），灵活控制上下文长度

### 批处理调度
- BatchTimeout 独立超时机制：新文本传入重置定时器，超时后自动发送
- MaxWordCount 超限立即触发发送，无需等待 BatchTimeout
- 修复并行发送失效问题，多批次可同时发送

### 兼容与稳定性
- 兼容 .NET 3.5（Environment.TickCount 替代 TickCount64）
- CI 跳过 ILRepack 避免 Mono.Cecil 兼容性问题
- CI 移除 Setup .NET 步骤，直接使用预装 SDK，节省约 46 秒

### 精简
- 移除 FuzzyString 模糊匹配库
- 移除文件式翻译DB扫描
- 移除 AGENTS.md 遗留文件

## 配置

在 `Config.ini` 文件中修改以下配置：

```
[Service]
Endpoint=AutoLLMTranslate
```

此外，你需要正确配置语言：

```
Language=zh-cn
FromLanguage=en
```

### 最小配置

```
[AutoLLM]
APIKey= <OPTION>
Model=qwen3:8b
URL=http://localhost:11434/v1
```

### 配置说明

| 参数 | 作用 | 默认值 |
|------|------|--------|
| `Model` | 翻译用的模型名 | `gpt-4o` |
| `URL` | LLM API 地址，以 `/v1` 或 `/chat/completions` 结尾 | `https://api.openai.com/v1/chat/completions` |
| `APIKey` | API 密钥，多个 key 用 `;` 隔开实现负载均衡 | 空 |
| `Requirement` | 额外的翻译指令，例如"使用莎士比亚风格翻译" | 空 |
| `Terminology` | 术语表，格式：`Lorien==罗林\|Skadi==斯卡蒂` | 空 |
| `GameName` | 游戏名称，帮助 AI 理解上下文 | `A Game` |
| `GameDesc` | 游戏描述，帮助 AI 更准确翻译 | 空 |
| `ModelParams` | 额外模型参数，JSON 格式，会合并到请求体中 | 空 |
| `HalfWidth` | 全角符号自动转半角 | `True` |
| `MaxWordCount` | 每批最大字符数，达到此值立即发送 | `2500` |
| `ParallelCount` | 最大并发翻译请求数 | `3` |
| `Interval` | 轮询间隔（毫秒） | `200` |
| `MaxRetry` | 翻译失败最大重试次数 | `10` |
| `DisableSpamChecks` | 禁用垃圾检查 | `False` |
| `LogLevel` | 日志等级：Error / Warning / Info / Debug | `Error` |
| `Log2File` | 是否输出日志到文件 | `False` |

> **本仓库新增配置：**
>
> | 参数 | 作用 | 默认值 |
> |------|------|--------|
> | `BatchTimeout` | 无新文本时的等待超时（毫秒），到期后即使未达到 MaxWordCount 也会发送 | `1000` |
> | `History` | 对话历史保留轮数，0=禁用，-1=无限制，正数=保留最近 N 轮。用于提高 API 缓存命中率 | `-1` |
