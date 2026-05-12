## 简介
基于 [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) 的个人定制版。

## 致谢
- [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) —— 插件基础
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) —— 上游仓库

## 快速开始
参照[上游仓库](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) 安装插件后，首次运行游戏会自动创建 `[AutoLLM]` 配置段，按需填写以下三项即可使用：

```ini
[AutoLLM]
Model=模型名字
URL=API地址
APIKey=API密钥
```

## 全部配置

| 参数 | 作用 | 默认值 | 说明 |
|---|---|---|---|
| Model | 模型名称 | （无） | 模型需支持 JSON 输出，不支持的效果差 |
| URL | API 地址 | （无） | |
| APIKey | API 密钥 | （无） | |
| MaxWordCount | 最大字符数 | `2500` | 触发后，下一句使用新批次 |
| ParallelCount | 并发数 | `1` | >1禁用对话历史，并发占满时，合并翻译，触发`MaxWordCount`后，下一句使用新批次 |
| MaxContext | 最大上下文（token） | `1024` | 触发后清空对话历史，推荐不超过 15000 |
| MaxRetry | 重试次数 | `10` | |
| ModelParams | 模型额外参数（JSON） | （无） | 如： `{"temperature":0.3}` |
| ExtraPrompt | 附加提示词 | （无） | 附加在`Config.cs`文件的{{EXTRA_PROMPT}}占位符 |
| HalfWidth | 全角转半角 | `True` | |
| DisableSpamChecks | 禁用 XUnity spam | `True` | 推荐`True`减少误关 |
| ~~LogLevel~~ | ~~日志等级~~ | — | 已移除，由`BepInEx.cfg`控制|
| ~~Log2File~~ | ~~日志输出到文件~~ | — | 已移除，统一输出`LogOutput.log` |
| ~~Terminology~~ | ~~术语表~~ | — | 已移除，改用`ExtraPrompt` |
| ~~GameName~~ | ~~游戏名称~~ | — | 已移除，不再写入 prompt |
| ~~GameDesc~~ | ~~游戏描述~~ | — | 已移除，不再写入 prompt |

## 相对于上游的主要改动

- **JSON 输出 + 流式解析**：强制 `response_format: json_object`，SSE 增量拼接，解析更稳定
- **对话历史**：批次间共享上下文以提升缓存命中率，超出 `MaxContext` 自动清空
- **Token 与速率统计**：每批次输出输入/输出/缓存 token 及速率，累计用量追踪
- **限速退避**：429 自动指数退避，不消耗重试次数；超时适配 DeepSeek 10 分钟等待窗口
- **资源优化**：ThreadPool 替代逐批次建线程，支持优雅关闭，修复连接泄露及线程安全问题
- **移除参数**：`LogLevel`、`Log2File`、`Terminology`、`GameName`、`GameDesc`，日志统一由 BepInEx.cfg 控制
