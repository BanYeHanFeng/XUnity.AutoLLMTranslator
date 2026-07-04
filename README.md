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
- 单文件拆分多个文件，减少维护压力

**事件驱动调度**
- 事件唤醒 + 50ms 保底轮询，降低延迟

**JSON 输出模式**
- 设置 JSON 输出模式所需要的参数，如模型支持 100%JSON 输出下，解决模型有概率输出错误格式带来的解析问题

**对话历史**
- 历史翻译使用对话历史，提升缓存命中率，降低历史翻译费用

**限速退避**
- API 限速 (429) 自动指数退避 (5s→10s→20s→40s→60s)
- 不消耗重试次数

**配置变更**
- 移除`LogLevel` `Log2File` `Terminology` `GameName` `GameDesc` `MaxWordCount` `Requirement` `Interval` `ParallelCount`
- 移除多 Key 负载均衡，`APIKey` 不再支持 `;` 分割轮询
- 日志等级由`BepInEx.cfg`统一管理，统一输出到`LogOutput.log`
- 新增`MaxContext`参数，自定义最大上下文长度
- 新增`CustomPrompt`参数，完全自定义系统提示词
- 新增`AutoGlossary`参数，模型输出译文时额外输出术语
- 精简默认提示词 (2947 字符数→170 字符数)

**日志**
- 增加输入/输出 token，缓存命中/未命中，Token 速度，耗时
- 对话历史状态 (轮数、清空次数、上下文估算) 
- 限速退避、任务积压 (>200 条) 
- 日志剔除不必要的内容，减少维护压力

## 常见问题
**问：如何安装本仓库插件**
<p>
  <b>- 答：</b><a href="docs/安装教程.md">安装教程</a><br>
  <b>- 注：</b>本插件暂不支持 IL2CPP ，后续可能会进行适配
</p>

**问：部分字体出现□□□的情况**
<p>
  <b>- 答：</b><a href="docs/更换字体教程.md">解决方法</a>
</p>

**问：模型默认开启思考，但思考又很慢，如何关闭**
<p>
  <b>- 答：</b><a href="docs/关闭思考教程.md">关闭方法</a><br>
  <b>- 注：</b>关闭思考会影响翻译质量，但能换来更快的响应
</p>

**问：推荐选择哪家模型**
<p>
  <b>- 答：</b> DeepSeek 吧，便宜<br>
  <b>- 注：</b>目前开发者只用过 glm 5.2 和 DeepSeek v4 系列
</p>

**问：如何本地部署模型**
<p>
  <b>- 答：</b>请去哔哩哔哩搜索相关教程，然后根据所设置的上下文，设置<code>MaxContext</code>参数<br>
  <b>- 注：</b>参数设置错误会导致翻译失败
</p>

## 全部配置
| 参数 | 默认值 | 说明 |
|---|---|---|
| Model | | 模型名称 |
| URL | | 接口网址。以`/v1`或`/v1/`后缀则自动补全至`/v1/chat/completions` |
| APIKey | | 接口密钥 |
| ModelParams | | 自定义模型参数，如：`{"temperature":0.3}` |
| MaxContext | `4096` | 上下文最大Token数。自动估算每条文本的 Token 消耗(收到 API 返回后校准,否则按 1 字符~ 0.75 token)。超限时分三种情况处理：①清空对话历史 ②超出部分分配到下一批 ③单条仍超出则丢弃并记录日志 |
| MaxRetry | `5` | 最大重试次数 |
| CustomPrompt | `False` | 是否开启自定义提示词，开启后配置生成在`游戏根目录/BepInEx/config/AutoLLM_CustomPrompt.txt`，有两套提示词，①是普通系统提示词，②是开启自动术语表后的系统提示词|
| AutoGlossary | `False` | 是否开启自动术语表，开启后 配置文件生成在`游戏根目录/BepInEx/config/AutoLLM_Glossary.txt`，①模型在翻译同时输出新术语 ②术语表通过占位符的方式进行注入，③等待历史对话清空后注入新的术语 |
| HalfWidth | `True` | 是否将全角字符转换为半角 |
| DisableSpamChecks | `True` | 是否禁用 AutoTranslator 框架垃圾检查 |
| ~~LogLevel~~ | 已移除 | ~~日志等级~~，由`BepInEx.cfg`控制 |
| ~~Log2File~~ | 已移除 | ~~日志输出文件~~，统一输出`LogOutput.log` |
| ~~Terminology~~ | 已移除 | ~~术语表~~ |
| ~~GameName~~ | 已移除 | ~~游戏名称~~ |
| ~~GameDesc~~ | 已移除 | ~~游戏描述~~ |
| ~~MaxWordCount~~ | 已移除 | ~~单批最大字符数~~ |
| ~~Requirement~~ | 已移除 | ~~额外翻译需求/指令~~ |
| ~~Interval~~ | 已移除 | ~~轮询间隔~~ |
| ~~ParallelCount~~ | 已移除 | ~~并行翻译数~~|