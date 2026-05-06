## 简介
基于 [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) 的个人定制版。

## 致谢
- [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) —— 插件基础
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) —— 上游仓库

## 快速开始
参照[上游仓库](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) 安装插件后，首次运行游戏会自动创建 `[AutoLLM]` 配置段，按需填写以下三项即可使用：

```ini
[AutoLLM]
Model = deepseek-chat
URL = https://api.deepseek.com/v1
APIKey = sk-xxxxxxxx
```

## 全部配置

| 参数 | 说明 | 默认值 |
|---|---|---|
| Model | 模型名称，需支持 JSON Output | （无） |
| URL | API 地址 | （无） |
| APIKey | API 密钥 | （无） |
| BatchTimeout | 新文本到达后的等待时间（毫秒），超时后发送翻译 | `1000` |
| MaxWordCount | 每批最大字符数，达到后立刻发送 | `2500` |
| ParallelCount | 并发数，>1 时自动禁用对话历史 | `1` |
| MaxContext | 模型上下文上限（token），超出自动清空对话历史。DeepSeek 1M 上下文填 `1000000` | `0` |
| MaxRetry | 翻译失败重试次数 | `10` |
| ModelParams | 额外模型参数（JSON 格式），如 `{"temperature":0.3}` | （无） |
| ExtraPrompt | 附加提示词，追加在默认提示词之后 | （无） |
| HalfWidth | 全角符号自动转半角 | `True` |
| DisableSpamChecks | 禁止 XUnity 的 spam 检测（翻译较慢时可能误关插件） | `True` |
| LogLevel | 日志等级：`Error` / `Warning` / `Info` / `Debug`，写入 `LogOutput.log` | `Error` |
| ~~Log2File~~ | 日志输出到文件（已移除，日志统一由 XuaLogger 输出到 LogOutput.log） | — |
| ~~Terminology~~ | 术语表（已移除，改用 ExtraPrompt） | — |
| ~~GameName~~ | 游戏名称（已移除，不再写入 prompt） | — |
| ~~GameDesc~~ | 游戏描述（已移除，不再写入 prompt） | — |

## 相对于上游的主要改动

- **翻译方式**：改为 JSON Output 模式，解析更稳定；提示词精简为中文
- **对话历史**：批次间共享上下文，提升缓存命中率；超出 `MaxContext` 自动清空
- **批处理**：`BatchTimeout` + `MaxWordCount` 双条件触发；并发 >1 时自动禁用历史避免冲突
- **Token 统计**：每批次输出输入/输出 token、速率、累计用量
- **Bug 修复**：修复了两个上游遗留 bug —— 已发送成功的翻译被误判重试（浪费一倍 token）、高重试次数任务可能永久等待
