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

| 参数 | 作用 | 默认值 | 说明 |
|---|---|---|---|
| Model | 模型名称，需支持 JSON Output | （无） | |
| URL | API 地址 | （无） | |
| APIKey | API 密钥 | （无） | |
| BatchTimeout | 新文本到达后的等待时间（毫秒），超时后发送翻译 | `-1` | `-1` 禁用，有文本立即处理不等待 |
| MaxWordCount | 每批最大字符数，达到后立刻发送 | `2500` | |
| ParallelCount | 并发数 | `1` | >1 时自动禁用对话历史 |
| MaxContext | 模型上下文上限（token），超出自动清空对话历史 | `1024` | 不宜超过 50000，过大时就算 100% 缓存命中也避免不了大基数带来的花费 |
| MaxRetry | 翻译失败重试次数 | `10` | |
| ModelParams | 额外模型参数（JSON 格式） | （无） | 如 `{"temperature":0.3}` |
| ExtraPrompt | 附加提示词 | （无） | 追加在默认提示词之后 |
| HalfWidth | 全角符号自动转半角 | `True` | |
| DisableSpamChecks | 禁止 XUnity 的 spam 检测 | `True` | 翻译较慢时可能误关插件 |
| ~~LogLevel~~ | ~~日志等级~~ | — | 已移除，改由 BepInEx/config/BepInEx.cfg 控制 |
| ~~Log2File~~ | ~~日志输出到文件~~ | — | 已移除，日志统一由 XuaLogger 输出到 LogOutput.log |
| ~~Terminology~~ | ~~术语表~~ | — | 已移除，改用 ExtraPrompt |
| ~~GameName~~ | ~~游戏名称~~ | — | 已移除，不再写入 prompt |
| ~~GameDesc~~ | ~~游戏描述~~ | — | 已移除，不再写入 prompt |

## 相对于上游的主要改动

- **JSON Output + 流式解析**：强制 `response_format: json_object`，SSE 增量拼接，解析更稳定
- **对话历史**：批次间共享上下文以提升缓存命中率，超出 `MaxContext` 自动清空
- **批处理与并发**：`BatchTimeout` / `MaxWordCount` 双条件触发，并发 >1 时自动禁用历史
- **Token 与速率统计**：每批次输出输入/输出/缓存 token 及速率，累计用量追踪
- **限速退避**：429 自动指数退避，不消耗重试次数；超时适配 DeepSeek 10 分钟等待窗口
- **资源优化**：ThreadPool 替代逐批次建线程，支持优雅关闭，修复连接泄露及线程安全问题
- **移除参数**：`LogLevel`、`Log2File`、`Terminology`、`GameName`、`GameDesc`，日志统一由 BepInEx.cfg 控制
