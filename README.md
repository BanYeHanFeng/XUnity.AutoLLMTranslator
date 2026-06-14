<p align="center">
  <a href="README.md">简体中文</a> |
  <a href="README.en.md">English</a>
</p>

## 简介
- **个人需求已基本完善，有问题请提交 issue ，看到 issue 我会回复并解决的**

## 致谢
- [bbepis/XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) **插件基础**
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) **上游仓库**

## 相对于上游的主要改动
**架构重构**
- 移除 HTTP 代理层，减少开销
- 单模块拆分多个文件，减少维护压力

**事件驱动调度**
- 事件唤醒 + 50ms 保底轮询，降低延迟

**JSON 输出模式**
- 设置 JSON 输出模式所需要的参数，模型支持100% JSON输出下，彻底解决模型有概率输出格式错误，导致解析失败的问题

**对话历史与缓存**
- 历史翻译使用对话历史，提升缓存命中率，降低历史翻译费用
- 多并行翻译时自动禁用对话历史，避免缓存前缀被修改导致缓存命中率大幅下降，可能会降低翻译质量

**并行与合并**
- `ParallelCount` 控制翻译请求数，并行占满时自动排队等待
- 排队期间，多条短文本自动合并成一个批次

**限速退避**
- API 限速 (429) 自动指数退避 (5s→10s→20s→40s→60s)
- 不消耗重试次数

**配置变更**
- 已移除：`LogLevel` `Log2File` `Terminology` `GameName` `GameDesc` `MaxWordCount`
- 日志等级由 `BepInEx.cfg` 统一管理，统一输出到 `LogOutput.log`
- 新增 `MaxContext` 参数，自定义最大上下文长度
- 新增 `CustomPrompt` 参数，完全自定义系统提示词
- 精简默认提示词 (2898 字符数→132 字符数)

**日志**
- 增加输入/输出 token，缓存命中/未命中，Token 速度，耗时
- 对话历史状态 (轮数、清空次数、上下文估算) 
- 限速退避、任务积压 (>200 条) 
- 日志剔除不必要的内容，减少维护压力

## 安装教程
<p align="center">
  <a href="docs/安装教程.md">安装教程</a>
</p>

## 常见问题
- 部分字体出现□□□的情况
<p align="center">
  <a href="docs/更换字体教程.md">解决方法</a>
</p>

## 全部配置
| 参数 | 默认值 | 说明 |
|---|---|---|
| Model | | 模型名称。模型原生支持 100% JSON 输出最佳(如DeepSeek) |
| URL | | 接口网址。以`/v1`后缀则自动补全至`/v1/chat/completions` |
| APIKey | | 接口密钥 |
| ModelParams | | 自定义模型参数，如： `{"temperature":0.3}` |
| ParallelCount | `1` | 并行翻译数。>1 时禁用对话历史，并发满后进行排队，排队期间，多条短文本自动合并成一个批次|
| MaxContext | `4096` | 上下文最大Token数。自动估算每条文本的 Token 消耗(收到 API 返回后校准,否则按0.75 字符~1 token)。超限时分三种情况处理:① 清空对话历史 ② 超出部分分配到下一批 ③ 单条仍超出则丢弃并记录日志 |
| MaxRetry | `10` | 最大重试次数 |
| CustomPrompt | `False` | 是否开启自定义提示词，开启后配置文件生成在`BepInEx/config/AutoLLM_CustomPrompt.txt` |
| HalfWidth | `True` | 是否将全角字符转换为半角 |
| DisableSpamChecks | `True` | 是否禁用 AutoTranslator 垃圾检查 |
| ~~LogLevel~~ | 已移除 | ~~日志等级~~。由`BepInEx.cfg`控制 |
| ~~Log2File~~ | 已移除 | ~~日志输出文件~~。统一输出`LogOutput.log` |
| ~~Terminology~~ | 已移除 | ~~术语表~~ |
| ~~GameName~~ | 已移除 | ~~游戏名称~~ |
| ~~GameDesc~~ | 已移除 | ~~游戏描述~~ |
| ~~MaxWordCount~~ | 已移除 | ~~单批最大字符数~~ |
| MaxRetry | `10` | 最大重试次数 |
| CustomPrompt | `False` | 是否开启自定义提示词，开启后配置文件生成在`BepInEx/config/AutoLLM_CustomPrompt.txt` |
| HalfWidth | `True` | 是否将全角字符转换为半角 |
| DisableSpamChecks | `True` | 是否禁用 AutoTranslator 垃圾检查 |
| ~~LogLevel~~ | 已移除 | ~~日志等级~~。由`BepInEx.cfg`控制 |
| ~~Log2File~~ | 已移除 | ~~日志输出文件~~。统一输出`LogOutput.log` |
| ~~Terminology~~ | 已移除 | ~~术语表~~ |
| ~~GameName~~ | 已移除 | ~~游戏名称~~ |
| ~~GameDesc~~ | 已移除 | ~~游戏描述~~ |
| ~~MaxWordCount~~ | 已移除 | ~~单批最大字符数~~ |
