## 简介
基于 [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) 的个人定制版。所有修改均由 AI 辅助完成。

## 致谢
- [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) —— 插件基础
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) —— 上游仓库

## 快速开始
请参照[上游仓库](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) 的安装说明完成插件安装。 

首次运行游戏后，插件会自动在配置文件中创建 `[AutoLLM]` 段，按需编辑以下配置项即可。

## 全部配置
| 参数 | 说明 | 默认值 |
|------|------|--------|
| Model | 模型名称，需模型支持JSON Output | （无） |
| URL | API 地址，以 `/v1` 或 `/chat/completions` 结尾 | （无） |
| APIKey | API 密钥 | （无） |
| BatchTimeout | 等待新文本定时器，超时后处理文本（毫秒） | `1000` |
| MaxWordCount | 每批最大字符数，达到即处理 | `2500` |
| History | 对话历史保留轮数：`0`=禁用，`-1`=无限制（推荐），正数=保留最近 N 轮。<br>设为正数会破坏缓存前缀连续性，大幅降低缓存命中率 | `-1` |
| ParallelCount | 并发翻译数量。设为 >1 会导致多个批次同时追加到共享对话历史，历史交错乱序，后续请求无法命中 KV 缓存前缀，大幅降低缓存命中率。推荐保持 `1` | `1` |
| MaxContext | 模型输入上下文窗口大小（token 数），不含输出。例如 DeepSeek 上下文 1M 填 `1000000`。对话历史超出限制时自动清空，防止 token 溢出。`0`=不限制 | `0` |
| MaxRetry | 翻译失败重试次数 | `10` |
| ModelParams | 额外模型参数（JSON 格式） | （无） |
| ExtraPrompt | 附加系统提示词，追加在默认提示词之后，用于术语表、风格描述等 | （无） |
| HalfWidth | 全角符号自动转半角 | `True` |
| DisableSpamChecks | 禁用垃圾文本过滤 | `False` |
| LogLevel | 日志等级：`Error` / `Warning` / `Info` / `Debug` | `Error` |
| Log2File | 是否输出日志到文件 | `False` |
## 相对于上游的主要改动

- **翻译质量**
  - 采用 JSON Output 模式，解析更稳定。
  - 使用连续对话历史替代历史翻译/模糊搜索，缓存命中率显著提升。
  - 可通过 `History` 控制上下文长度。
  - 移除硬编码的 `temperature` / `max_tokens`。
  - 以中文精简提示词替代原英文提示词。
  - 移除 GameName、GameDesc、Requirement 等原 prompt 占位符。
  - 支持 `ExtraPrompt` 附加提示词，用于术语表、风格描述等。
- **批处理调度**
  - `BatchTimeout` 与 `MaxWordCount` 双条件触发发送。
  - 修复并行发送失效问题，多批次可同时请求。
  - `ParallelCount` 默认值改为 `1`，避免并行导致对话历史交错、KV 缓存前缀无法命中。
- **兼容与精简**
  - 兼容 .NET 3.5（`Environment.TickCount` 替代 `TickCount64`）。
  - CI 跳过 ILRepack，避免 Mono.Cecil 兼容性问题，并移除 Setup .NET 步骤以加速构建。
  - 移除 FuzzyString 模糊匹配库及文件式翻译数据库扫描。
  - 移除 Interval 配置，由 BatchTimeout 完全替代轮询合并功能。
  - 移除 GameName、GameDesc、Requirement prompt 占位符。
  - 移除 Terminology 术语表模块。
  - APIKey 改为单 key 配置。
  - HTTP 请求添加 60s 超时及 Expect100Continue = false。
