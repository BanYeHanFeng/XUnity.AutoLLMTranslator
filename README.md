## 简介

我本人并不懂代码，所有代码都用ai编程，可能会出现意料之外的bug。

## 致谢

该插件基于 XUnity.AutoTranslator(https://github.com/bbepis/XUnity.AutoTranslator) 开发。
上游仓库：https://github.com/NothingNullNull/XUnity.AutoLLMTranslator
同时也使用了FuzzyString(https://github.com/kdjones/fuzzystring)来实现文本搜索。
感谢他们的付出。

## 配置

在 `Config.ini` 文件中修改以下配置：
```
    [Service]
    Endpoint=AutoLLMTranslate
```
此外，你需要正确的配置：
```
Language=zh-cn
FromLanguage=en
```
### 范例

```
[Service]
Endpoint=AutoLLMTranslate

[General]
Language=zh_cn
FromLanguage=en
```

最小配置：

```
[AutoLLM]
APIKey= <OPTION>  
Model=qwen3:8b
URL=http://localhost:11434/v1
```

完整配置：

```
[AutoLLM]
APIKey= <OPTION>  
Model=qwen-turbo  
URL=https://dashscope.aliyuncs.com/compatible-mode/v1  
Requirement=/no_think  
Terminology=
GameName=DeathMustDie  
GameDesc=一个刷装备打怪的游戏、暗黑破坏神的风格和元素  
ModelParams={"temperature":0.1}
HalfWidth=True  
MaxWordCount=500  
ParallelCount=3  
Interval=200
BatchTimeout=1
MaxRetry=10
LogLevel=Error
Log2File=False
```

配置说明：
- `Model`：用于翻译的模型。
- `URL`：LLM 服务器的 URL，一般以/v1结尾。也可以是/chat/completions的完整路径。
- [OPTION]`APIKey`：LLM 服务器的 API 密钥。如果使用本地模型，可以留空。你可以使用;分割多个key来实现负载均衡。
- [OPTION]`Requirement`：额外的翻译需求或指令，例如:使用莎士比亚的风格进行翻译。
- [OPTION]`Terminology`：术语表，使用|隔开不同术语，使用==连接原文和翻译。例如：Lorien==罗林|Skadi==斯卡蒂。
- [OPTION]`GameName`: 游戏名字。
- [OPTION]`GameDesc`：游戏介绍，用于帮助AI进行更准确的翻译，可以对游戏的玩法/类型/风格进行描述。
- [OPTION]`ModelParams`: 模型参数定制，使用json格式书写，会直接传递给模型api。例如：{"temperature":0.1}
- [OPTION]`MaxWordCount`：每批翻译的最大单词数，适当的单词可以减少并发数量从而提高翻译速度。
- [OPTION]`ParallelCount`：并行翻译任务的最大数量，一般由LLM的提供商决定。
- [OPTION]`Interval`：轮询间隔（毫秒）,每次翻译的间隔，在间隔中系统会尽可能的合并翻译内容，以便提高翻译速度减少并发，但太长会导致响应不够及时。
- [OPTION]`BatchTimeout`：批量翻译等待超时（秒），没有新文本传入时等待此时间后才开始翻译，默认1秒。
- [OPTION]`HalfWidth`：是否将全角字符转换为半角，在字体无法显示全角符号的时候使用这个。
- [OPTION]`MaxRetry`：失败翻译的最大重试次数，一般不动，如果大模型失败率太高，可以尝试提高。
- [OPTION]`DisableSpamChecks`: 禁用垃圾检查，默认False。
- [OPTION]`LogLevel`：日志等级，Error/Warning/Info/Debug。
- [OPTION]`Log2File`：是否将日志写入文件。
