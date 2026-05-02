## 简介

基于上游 NothingNullNull/XUnity.AutoLLMTranslator 根据自己需求二改的
我本人并不懂代码，所有代码都用ai编程，可能会出现意料之外的bug

具体使用方法请看上游仓库(https://github.com/NothingNullNull/XUnity.AutoLLMTranslator)

## 致谢

```
XUnity.AutoTranslator 该插件基于此项目开发
NothingNullNull/XUnity.AutoLLMTranslator 上游仓库
FuzzyString 实现文本搜索
```

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

### 完整配置

```
[AutoLLM]
APIKey= <OPTION>
Model=qwen-turbo
URL=https://dashscope.aliyuncs.com/compatible-mode/v1
Requirement=
Terminology=
GameName=DeathMustDie
GameDesc=一个刷装备打怪的游戏、暗黑破坏神的风格和元素
ModelParams={"temperature":0.1}
HalfWidth=True
MaxWordCount=500
ParallelCount=3
Interval=200
BatchTimeout=1000
MaxRetry=10
LogLevel=Error
Log2File=False
```

### 配置说明

| 参数 | 说明 |
|------|------|
| `Model` | 用于翻译的模型。 |
| `URL` | LLM 服务器的 URL，一般以 `/v1` 结尾，也可以是 `/chat/completions` 的完整路径。 |
| `APIKey` | LLM 服务器的 API 密钥，如果使用本地模型可以留空。使用 `;` 分隔多个 key 可实现负载均衡。 |
| `Requirement` | 额外的翻译需求或指令，例如使用莎士比亚的风格进行翻译。 |
| `Terminology` | 术语表，使用 `\|` 隔开不同术语，使用 `==` 连接原文和翻译。例如：`Lorien==罗林\|Skadi==斯卡蒂`。 |
| `GameName` | 游戏名字。 |
| `GameDesc` | 游戏介绍，用于帮助AI进行更准确的翻译，可以对游戏的玩法/类型/风格进行描述。 |
| `ModelParams` | 模型参数定制，使用 JSON 格式书写，会直接传递给模型 API。例如：`{"temperature":0.1}`。 |
| `MaxWordCount` | 每批翻译的最大单词数，适当的单词可以减少并发数量从而提高翻译速度。 |
| `ParallelCount` | 并行翻译任务的最大数量，一般由 LLM 的提供商决定。 |
| `Interval` | 轮询间隔（毫秒），每次翻译的间隔，在间隔中系统会尽可能合并翻译内容。太长会导致响应不及时。 |
| `HalfWidth` | 是否将全角字符转换为半角，在字体无法显示全角符号时使用。 |
| `MaxRetry` | 失败翻译的最大重试次数，如果大模型失败率太高可以适当提高。 |
| `DisableSpamChecks` | 禁用垃圾检查，默认 False。 |
| `LogLevel` | 日志等级，可选：Error / Warning / Info / Debug。 |
| `Log2File` | 是否将日志写入文件。 |

> **本仓库新增/改进配置：**
>
> | 参数 | 说明 |
> |------|------|
> | `BatchTimeout` | 批量翻译等待超时（毫秒），没有新文本传入时等待此时间后才开始翻译，默认 1000 毫秒。 |
