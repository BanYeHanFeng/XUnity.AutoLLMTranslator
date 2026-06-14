<p align="center">
  <a href="README.md">简体中文</a> |
  <a href="README.en.md">English</a>
</p>

## Introduction
- **My personal requirements have been mostly fulfilled. If you encounter any issues, please submit an issue and I will respond and resolve it.**

## Acknowledgments
- [bbepis/XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) **Plugin Foundation**
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) **Upstream Repository**

## Major Changes Compared to Upstream
**Architecture Refactoring**
- Removed HTTP proxy layer, reduced overhead
- Split single module into multiple files, reduced maintenance burden

**Event-Driven Scheduling**
- Event-driven wake-up + 50ms fallback polling, reduced latency

**JSON Output Mode**
- Configure parameters required for JSON output mode. When the model supports 100% JSON output, it completely resolves the issue of the model occasionally outputting malformed formats that cause parsing failures.

**Conversation History & Cache**
- Use conversation history for repeated translations, improving cache hit rates and reducing costs for repeated translations
- Automatically disable conversation history during parallel translations to prevent cache prefix changes that would significantly degrade cache hit rates (may reduce translation quality)

**Parallelism & Merging**
- `ParallelCount` controls the number of translation requests; automatically queues when parallel slots are full
- During queuing, multiple short texts are automatically merged into a single batch

**Rate Limiting Backoff**
- API rate limiting (429) automatically triggers exponential backoff (5s → 10s → 20s → 40s → 60s)
- Does not consume retry attempts

**Configuration Changes**
- Removed: `LogLevel` `Log2File` `Terminology` `GameName` `GameDesc` `MaxWordCount`
- Log level is now managed by `BepInEx.cfg`, unified output to `LogOutput.log`
- Added `MaxContext` parameter for custom max context length
- Added `CustomPrompt` parameter for fully custom system prompts
- Simplified default prompt (2898 chars → 132 chars)

**Logging**
- Added input/output tokens, cache hit/miss, token speed, elapsed time
- Conversation history status (rounds, clears, context estimates)
- Rate limit backoff, task backlog (>200 items)
- Removed unnecessary log content to reduce maintenance burden

## Installation Guide
<p align="center">
  <a href="docs/安装教程.en.md">Installation Guide</a>
</p>

## FAQ
- Some fonts display as □□□ (missing characters)
<p align="center">
  <a href="docs/更换字体教程.en.md">Solution Guide</a>
</p>

## All Configuration Options
| Parameter | Default | Description |
|---|---|---|
| Model | | Model name. Models with native 100% JSON output support are recommended (e.g., DeepSeek) |
| URL | | API endpoint URL. If the URL ends with `/v1`, it will be auto-completed to `/v1/chat/completions` |
| APIKey | | API key |
| ModelParams | | Custom model parameters, e.g.: `{"temperature":0.3}` |
| ParallelCount | `1` | Number of parallel translations. When >1, conversation history is disabled. When parallel slots are full, tasks are queued and multiple short texts are automatically merged into one batch |
| MaxContext | `4096` | Maximum context token count. Automatically estimates token consumption per text (calibrated after receiving API response; otherwise estimated at ~0.75 chars per token). Three handling modes when exceeded: ① Clear conversation history ② Overflow distributed to next batch ③ Single text still exceeding limit is discarded and logged |
| MaxRetry | `10` | Maximum retry attempts |
| CustomPrompt | `False` | Whether to enable custom prompts. When enabled, the configuration file is created at `BepInEx/config/AutoLLM_CustomPrompt.txt` |
| HalfWidth | `True` | Whether to convert fullwidth characters to halfwidth |
| DisableSpamChecks | `True` | Whether to disable AutoTranslator spam checks |
| ~~LogLevel~~ | Removed | ~~Log level~~. Managed by `BepInEx.cfg` |
| ~~Log2File~~ | Removed | ~~Log output file~~. Unified output to `LogOutput.log` |
| ~~Terminology~~ | Removed | ~~Terminology table~~ |
| ~~GameName~~ | Removed | ~~Game name~~ |
| ~~GameDesc~~ | Removed | ~~Game description~~ |
| ~~MaxWordCount~~ | Removed | ~~Max characters per batch~~ |
