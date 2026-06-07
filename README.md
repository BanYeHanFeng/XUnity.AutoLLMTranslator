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

### 翻译
**架构重构**
- 移除 HTTP 代理层 (HttpListener) ，直接实现 `ITranslateEndpoint`，消除中间人开销；663 行单体拆分为 15 个职责单一的文件，仅保留一个接口

**事件驱动调度**
- `AutoResetEvent` 唤醒 + 50ms 保底轮询，新任务到达即时响应，降低延迟和空转消耗

**JSON Output 模式**
- 设置 `response_format: json_object` + 默认提示词 JSON 示例，提升模型以 JSON 格式输出概率 (模型原生支持 100% JSON 输出最佳，如DeepSeek) 

**自定义提示词**
- 新增 `CustomPrompt` 参数，开启后从 `AutoLLM_CustomPrompt.txt` 文件，读取自定义提示词

**对话历史与缓存**
- 批次间共享上下文，利用 KV 缓存降低历史翻译费用
- 多并发时自动禁用对话历史

**并发与合并**
- `ParallelCount` 控制并发请求数，并发占满时自动排队等待
- 排队等待期间多条短文本自动合并为一批翻译
- 重试任务允许合批

**限速退避**
- API 限速 (429) 自动指数退避 (5s→10s→20s→40s→60s)
- 不消耗重试次数
---
### 配置与参数
**已移除**：`LogLevel` `Log2File` `Terminology` `GameName` `GameDesc` `MaxWordCount`

**配置变更**
- 日志等级由 `BepInEx.cfg` 统一管理，统一输出到 `LogOutput.log`
- Model/URL 留空时禁用翻译，APIKey 留空时跳过 Authorization 头
---
### 日志
- 无冗余前缀：每条日志仅含框架统一前缀 `[INFO : XUnity.Common]`，无 `[ALLM_X]` 和重复时间戳
- Info 级安静：成功批次 Info 0 条，仅在异常/重试/限速时输出
- Debug 级详细：成功批次输出「批次开始」和「批次完成」两条 Debug，含字符数、上下文占用、排队耗时、token 消耗、缓存命中、速率统计
- 中文表达统一：全中文描述（字符、毫秒、tokens/s），无中英混杂
- 异常堆栈完整：`Error(msg, ex)` 重载保留完整异常堆栈，替代手动拼接 `.ToString()`
- 错误体截断：服务器错误响应体超 200 字符时自动截断，防止刷屏

## 快速开始
参照[上游仓库](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) 使用BepinEx方式安装插件后，首次运行游戏会自动创建 `[AutoLLM]` 配置段，按需填写以下三项即可使用：

```ini
[AutoLLM]
Model=模型名字
URL=API地址
APIKey=API密钥
```

## 全部配置
| 参数 | 默认值 | 说明 |
|---|---|---|
| Model | | 模型名称。模型原生支持 100% JSON 输出最佳(如DeepSeek) |
| URL | | API URL。以`/v1`后缀则自动补全至`/v1/chat/completions` |
| APIKey | | API 密钥。留空时跳过 Authorization 头 |
| ParallelCount | `1` | 并发数。>1 时禁用对话历史，并发满后进行排队，排队区间短文本会合并成一个批次|
| MaxContext | `4096` | 上下文最大Token数。自动估算每条文本的 Token 消耗(收到 API 返回后校准,否则按 0.75 字符~1 token 估算)。超限时分三种情况处理:① 清空对话历史 ② 超出部分分配到下一批 ③ 单条仍超出则丢弃并记录日志 |
| MaxRetry | `10` | 最大重试次数 |
| ModelParams | | 自定义模型参数，如： `{"temperature":0.3}` |
| CustomPrompt | `False` | 是否开启自定义提示词，开启后配置文件生成在`BepInEx/config/AutoLLM_CustomPrompt.txt` |
| HalfWidth | `True` | 是否将全角字符转换为半角 |
| DisableSpamChecks | `True` | 是否禁用 AutoTranslator 垃圾检查 |
| ~~LogLevel~~ | 已移除 | ~~日志等级~~。由`BepInEx.cfg`控制 |
| ~~Log2File~~ | 已移除 | ~~日志输出文件~~。统一输出`LogOutput.log` |
| ~~Terminology~~ | 已移除 | ~~术语表~~ |
| ~~GameName~~ | 已移除 | ~~游戏名称~~ |
| ~~GameDesc~~ | 已移除 | ~~游戏描述~~ |
| ~~MaxWordCount~~ | 已移除 | ~~单批最大字符数~~ |
