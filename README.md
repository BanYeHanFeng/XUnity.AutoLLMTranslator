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
**文本**
- 事件唤醒，极低延迟获取文本
- 100ms 后无新文本才发送给模型，缓解文本碎片化问题

**模型**
- 设置 100%JSON 输出参数，解决模型有概率输出错误格式问题，需模型支持
- 对话历史替代历史翻译，降低历史翻译费用

**配置**
- 移除`LogLevel` `Log2File` `Terminology` `GameName` `GameDesc` `MaxWordCount` `Requirement` `Interval` `ParallelCount`
- 移除多 Key 负载均衡，`APIKey` 不再支持 `;` 分割轮询
- 日志等级由`BepInEx.cfg`统一管理，统一输出到`LogOutput.log`
- 新增`MaxContext`参数，自定义最大上下文长度
- 新增`CustomPrompt`参数，完全自定义系统提示词
- 新增`AutoGlossary`参数，模型输出译文时额外输出术语
- 精简默认提示词，2947 字符数 → 171 字符数(普通模式) / 273 字符数(术语表模式)

**其他**
- 移除 HTTP 代理层，减少开销

## 常见问题
**问：如何安装本仓库插件**
<p>
  <b>- 答：</b><a href="docs/安装.md">安装教程</a><br>
  <b>- 注：</b>本插件暂不支持 IL2CPP 技术的游戏，后续可能会进行适配
</p>

**问：部分字体出现「□□□」的情况**
<p>
  <b>- 答：</b><a href="docs/更换字体.md">解决方法</a>
</p>

**问：模型输出角色名不稳定，如何解决**
<p>
  <b>- 答：</b><a href="docs/术语表.md">自动术语表</a><br>
  <b>- 注：</b>开启自动术语表后响应约慢十几秒，看取舍
</p>

**问：模型默认开启思考，但思考过慢，如何关闭**
<p>
  <b>- 答：</b><a href="docs/关闭思考.md">关闭方法</a><br>
  <b>- 注：</b>关闭思考会影响翻译质量和自动术语表质量，但能换来更快的响应
</p>

**问：推荐选择哪家模型**
<p>
  <b>- 答：</b>deepseek v4 flash 吧<br>
  <b>- 注：</b>目前开发者只用过 glm 5.2 和 DeepSeek v4 系列，翻译测试均用deepseek v4 flash
</p>

**问：如何本地部署模型**
<p>
  <b>- 答：</b>请去<code>哔哩哔哩</code>搜索相关教程，然后根据所设置的上下文，设置<code>MaxContext</code>参数<br>
  <b>- 注：</b>参数设置错误会导致翻译失败
</p>

## 全部配置
| 参数 | 默认值 | 说明 |
|---|---|---|
| Model | | 模型名称 |
| URL | | 接口网址。以`/v1`或`/v1/`后缀则自动补全至`/v1/chat/completions` |
| APIKey | | 接口密钥 |
| ModelParams | | 自定义模型参数，如：`{"temperature":0.3}` |
| MaxContext | `4096` | 上下文最大Token数。自动估算每条文本的 Token 消耗(收到 API 返回后校准,否则按 1 字符~ 0.75 token)。超限时分三种情况处理：①清空对话历史 ②超出部分分配到下一批 ③单条超出则丢弃并记录日志 |
| MaxRetry | `5` | 最大重试次数 |
| CustomPrompt | `False` | 是否开启自定义提示词，开启后配置生成在`游戏根目录/BepInEx/config/AutoLLM_CustomPrompt.txt`，有两套提示词，`[普通模式提示词]` 下为普通系统提示词，`[自动术语表模式提示词]` 下为开启自动术语表后的系统提示词|
| AutoGlossary | `False` | 是否开启自动术语表，开启后术语表文件生成在`游戏根目录/BepInEx/config/AutoLLM_Glossary.json`，①模型在翻译同时解析新术语 ②术语表通过系统提示词的占位符的方式进行注入，仅在空历史注入 |
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