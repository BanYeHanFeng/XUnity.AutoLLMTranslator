<p align="center">
  <a href="README.md">简体中文</a> |
  <a href="README.en.md">English</a>
</p>

## 简介
- **个人需求也完善，有问题请提交issue**

## 致谢
- [bbepis/XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) **插件基础**
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) **上游仓库**

## 相对于上游的主要改动
- **架构重构**：移除 HTTP 代理层（HttpListener），直接实现 `ITranslateEndpoint`，消除中间人开销；663 行单体拆分为 15 个职责单一的文件，仅保留一个接口
- **JSON 输出模式**：要求 LLM 以 JSON 格式输出翻译结果，搭配流式增量解析，避免格式错乱导致的翻译失败
- **对话历史与缓存复用**：多批翻译共享上下文，充分利用 LLM 缓存机制降低重复翻译费用；超出上限自动清空历史
- **Token 用量统计**：实时显示每批翻译的输入/输出 Token 消耗及缓存命中情况
- **自定义系统提示词**：支持从本地 `.txt` 文件读取完全自定义的翻译风格和规则，首次开启自动生成默认模板（`BepInEx/config/AutoLLM_CustomPrompt.txt`）
- **限速自动退避**：遇到 API 限速时自动等待后重试（等待时间逐渐加长），不消耗重试次数
- **批量合并**：多条短文本自动合并为一轮翻译
- **事件驱动调度**：`AutoResetEvent` 唤醒 + 50ms 保底轮询，新任务到达即时响应，降低延迟和空转消耗
- **日志分级控制**：日志等级由 BepInEx 统一配置文件管理
- **精简配置项**：移除术语表、游戏名称/描述等已不使用的参数

## 快速开始
参照[上游仓库](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) 使用BepinEx安装插件后，首次运行游戏会自动创建 `[AutoLLM]` 配置段，按需填写以下三项即可使用：

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
| ParallelCount | 并发数 | `1` | >1禁用对话历史，并发占满时，合并排队翻译，批次字符数超过`MaxWordCount`后，下一句使用新批次 |
| MaxContext | 最大上下文（token） | `1024` | 触发后清空对话历史，推荐不超过 15000 |
| MaxRetry | 重试次数 | `10` | |
| ModelParams | 模型额外参数（JSON） | （无） | 如： `{"temperature":0.3}` |
| CustomPrompt | 自定义系统提示词 | `False` | 开启后，配置文件在`BepInEx/config/AutoLLM_CustomPrompt.txt` |
| HalfWidth | 全角转半角 | `True` | |
| DisableSpamChecks | 禁用 XUnity spam | `True` | 推荐`True`减少误关 |
| ~~LogLevel~~ | ~~日志等级~~ | — | 已移除，由`BepInEx.cfg`控制 |
| ~~Log2File~~ | ~~日志输出到文件~~ | — | 已移除，统一输出`LogOutput.log` |
| ~~Terminology~~ | ~~术语表~~ | — | 已移除 |
| ~~ExtraPrompt~~ | ~~附加提示词~~ | — | 已移除，改用`CustomPrompt` |
| ~~GameName~~ | ~~游戏名称~~ | — | 已移除 |
| ~~GameDesc~~ | ~~游戏描述~~ | — | 已移除 |
