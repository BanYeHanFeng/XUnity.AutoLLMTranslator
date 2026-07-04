<p align="center">
  <a href="README.md">简体中文</a> |
  <a href="README.en.md">English</a>
</p>

## Introduction
- **My personal needs are mostly met. If you encounter any issues, please submit an issue, and I will respond and resolve it.**

## Acknowledgments
- [bbepis/XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) **Plugin foundation**
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) **Upstream repository**

## Major Changes Compared to Upstream
**Architecture Refactoring**
- Removed the HTTP proxy layer, reducing overhead
- Split single module into multiple files, reducing maintenance burden

**Event-Driven Scheduling**
- Event wake-up + 50ms fallback polling, reducing latency

**JSON Output Mode**
- Configure parameters required for JSON output mode. If the model supports 100% JSON output, this solves parsing issues caused by the model occasionally outputting incorrect formats.

**Conversation History & Caching**
- Uses conversation history for repeated translations, improving cache hit rate and reducing costs
- Automatically disables conversation history during parallel translation to prevent cache prefix modification from significantly degrading cache hit rate and translation quality

**Parallelism & Batching**
- `ParallelCount` controls the number of parallel translations; when fully occupied, tasks automatically queue up
- During queuing, multiple short texts are automatically merged into a single batch

**Rate Limit Backoff**
- API rate limiting (429) triggers automatic exponential backoff (5s→10s→20s→40s→60s)
- Does not consume retry attempts

**Configuration Changes**
- Removed `LogLevel` `Log2File` `Terminology` `GameName` `GameDesc` `MaxWordCount` `Requirement` `Interval`
- Removed multi-key load balancing; `APIKey` no longer supports `;`-separated round-robin
- Log level is now managed uniformly by `BepInEx.cfg`, output to `LogOutput.log`
- Added `MaxContext` parameter for custom maximum context length
- Added `CustomPrompt` parameter for fully customizable system prompts
- Added `AutoGlossary` parameter for automatic glossary (model outputs terms alongside translations; terms are saved when conversation history is cleared)
- Streamlined default prompt (2947 chars → 170 chars)

**Logging**
- Added input/output token counts, cache hit/miss, token speed, elapsed time
- Conversation history status (rounds, clear count, context estimation)
- Rate limit backoff, task backlog (>200 tasks)
- Removed unnecessary log content to reduce maintenance burden

## FAQ

**Q: How do I install this plugin?**
<p>
  <b>- A:</b> <a href="docs/安装教程.en.md">Installation Guide</a><br>
  <b>- Note:</b> This plugin currently does not support IL2CPP; adaptation may come in the future.
</p>

**Q: Some fonts show as □□□**
<p>
  <b>- A:</b> <a href="docs/更换字体教程.en.md">Solution</a>
</p>

**Q: The model enables thinking by default, but thinking is slow. How do I disable it?**
<p>
  <b>- A:</b> <a href="docs/关闭思考教程.en.md">How to disable</a><br>
  <b>- Note:</b> Disabling thinking will affect translation quality, but will provide faster responses.
</p>

**Q: Which model provider do you recommend?**
<p>
  <b>- A:</b> DeepSeek, it's cheap.<br>
  <b>- Note:</b> The developer has only used glm 5.2 and DeepSeek v4 series so far.
</p>

**Q: How do I deploy a local model?**
<p>
  <b>- A:</b> Please search for tutorials on Bilibili, then set the <code>MaxContext</code> parameter according to the context you configured.<br>
  <b>- Note:</b> Incorrect parameter settings will cause translation failures.
</p>

## All Configuration Options
| Parameter | Default | Description |
|---|---|---|
| Model | | Model name |
| URL | | API endpoint. If suffixed with `/v1` or `/v1/`, it is auto-completed to `/v1/chat/completions` |
| APIKey | | API key |
| ModelParams | | Custom model parameters, e.g., `{"temperature":0.3}` |
| ParallelCount | `1` | Number of parallel translations. When >1, conversation history is disabled. When concurrency is full, tasks queue up; during queuing, multiple short texts are automatically merged into one batch. |
| MaxContext | `4096` | Maximum context token count. Automatically estimates token consumption per text (calibrated after receiving API response; otherwise estimated at ~0.75 token per character). When exceeded, three scenarios apply: ① Clear conversation history ② Overflow distributed to next batch ③ If a single text still exceeds, it is discarded and logged. |
| MaxRetry | `5` | Maximum retry attempts |
| CustomPrompt | `False` | Whether to enable custom prompts. When enabled, the config file is generated at `GameRoot/BepInEx/config/AutoLLM_CustomPrompt.txt` |
| AutoGlossary | `False` | Whether to enable automatic glossary. When enabled: ① The model outputs new terms alongside translations ② The glossary is part of the system prompt; new terms accumulate with conversation history ③ New terms are merged to `GameRoot/BepInEx/config/AutoLLM_Glossary.txt` (JSON format) only when conversation history is cleared ④ You can edit `GameRoot/BepInEx/config/AutoLLM_CustomGlossaryPrompt.txt` to customize the glossary prompt |
| HalfWidth | `True` | Whether to convert fullwidth characters to halfwidth |
| DisableSpamChecks | `True` | Whether to disable AutoTranslator framework spam checks |
| ~~LogLevel~~ | Removed | ~~Log level~~. Controlled by `BepInEx.cfg` |
| ~~Log2File~~ | Removed | ~~Log output file~~. Unified to `LogOutput.log` |
| ~~Terminology~~ | Removed | ~~Terminology~~ |
| ~~GameName~~ | Removed | ~~Game name~~ |
| ~~GameDesc~~ | Removed | ~~Game description~~ |
| ~~MaxWordCount~~ | Removed | ~~Max characters per batch~~ |
| ~~Requirement~~ | Removed | ~~Extra translation requirements/instructions~~ |
| ~~Interval~~ | Removed | ~~Polling interval~~ |
