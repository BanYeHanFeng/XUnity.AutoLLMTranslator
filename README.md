## 简介

基于上游 NothingNullNull/XUnity.AutoLLMTranslator 根据自己需求二改的
我本人并不懂代码，所有代码都用ai编程，可能会出现意料之外的bug

具体使用方法请看上游仓库(https://github.com/NothingNullNull/XUnity.AutoLLMTranslator)

## 致谢

```
XUnity.AutoTranslator 该插件基于此项目开发
NothingNullNull/XUnity.AutoLLMTranslator 上游仓库
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

### 配置说明

| 参数 | 作用 | 默认值 |
|------|------|--------|
| `Model` | 翻译用的模型名 | `gpt-4o` |
| `URL` | LLM API 地址，以 `/v1` 或 `/chat/completions` 结尾 | `https://api.openai.com/v1/chat/completions` |
| `APIKey` | API 密钥，多个 key 用 `;` 隔开实现负载均衡 | 空 |
| `Requirement` | 额外的翻译指令，例如"使用莎士比亚风格翻译" | 空 |
| `Terminology` | 术语表，格式：`Lorien==罗林\|Skadi==斯卡蒂` | 空 |
| `GameName` | 游戏名称，帮助 AI 理解上下文 | `A Game` |
| `GameDesc` | 游戏描述，帮助 AI 更准确翻译 | 空 |
| `ModelParams` | 额外模型参数，JSON 格式，会合并到请求体中 | 空 |
| `HalfWidth` | 全角符号自动转半角 | `True` |
| `MaxWordCount` | 每批最大字符数，达到此值立即发送 | `500` |
| `ParallelCount` | 最大并发翻译请求数 | `3` |
| `Interval` | 轮询间隔（毫秒） | `200` |
| `MaxRetry` | 翻译失败最大重试次数 | `10` |
| `DisableSpamChecks` | 禁用垃圾检查 | `False` |
| `LogLevel` | 日志等级：Error / Warning / Info / Debug | `Error` |
| `Log2File` | 是否输出日志到文件 | `False` |

> **本仓库新增配置：**
>
> | 参数 | 作用 | 默认值 |
> |------|------|--------|
> | `BatchTimeout` | 无新文本时的等待超时（毫秒），到期后即使未达到 MaxWordCount 也会发送 | `1000` |
> | `History` | 对话历史保留轮数，0=禁用，-1=无限制，正数=保留最近 N 轮。用于提高 API 缓存命中率 | `10` |
