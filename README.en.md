<p align="center">
  <a href="README.md">简体中文</a> |
  <a href="README.en.md">English</a>
</p>

## Overview
- **Built for personal needs; feel free to submit an issue for any problems**

## Acknowledgments
- [bbepis/XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) — **Plugin foundation**
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) — **Upstream repository**

## Key Changes from Upstream
**Architecture Overhaul**
- Removed HTTP proxy layer (HttpListener), reduced overhead
- Split monolithic module into multiple files, eased maintenance

**Event-Driven Scheduling**
- Event-based wakeup + 50ms fallback polling, reduced latency

**JSON Output Mode**
- Configured parameters enforce JSON output format; with models that support 100% JSON output, format errors are fully eliminated

**Conversation History & Cache**
- Historical translations leverage conversation history, improving cache hit rates and reducing costs
- Automatically disabled during parallel translation to prevent cache prefix changes from degrading hit rates; may affect translation quality

**Concurrency & Batching**
- `ParallelCount` controls concurrent request count; automatically queues when fully occupied
- Multiple short texts are automatically merged into a single batch while queued

**Rate Limit Backoff**
- API rate limits (429) trigger automatic exponential backoff (5s→10s→20s→40s→60s)
- Does not consume retry attempts

**Configuration Changes**
- Removed: `LogLevel` `Log2File` `Terminology` `GameName` `GameDesc` `MaxWordCount`
- Log level managed by `BepInEx.cfg`, unified output to `LogOutput.log`
- Added `CustomPrompt` parameter for fully customizable system prompts

**Logging**
- Added input/output tokens, cache hit/miss, token speed, elapsed time
- Conversation history status (rounds, clear count, context estimation)
- Rate limit backoff, task backlog (>200 items)
- Removed unnecessary log content to reduce maintenance burden

## Quick Start
Install the plugin via BepInEx as described in the [upstream repository](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator). On first game launch, the `[AutoLLM]` config section is automatically created. Fill in the following three items to get started:

```ini
[AutoLLM]
Model=model-name
URL=api-url
APIKey=api-key
```

## All Configuration
| Parameter | Default | Description |
|---|---|---|
| Model | | Model name. Models with native 100% JSON output support are recommended (e.g., DeepSeek) |
| URL | | API endpoint. Suffix `/v1` is auto-completed to `/v1/chat/completions` |
| APIKey | | API key |
| ParallelCount | `1` | Concurrent translation count. Disables conversation history when >1; automatically queues when fully occupied; short texts merge into a batch while queued |
| MaxContext | `4096` | Max context tokens. Estimates token usage per text (calibrated after API response; otherwise estimated at 0.75 chars per token). Overflow handling: ① Clear history ② Overflow goes to next batch ③ Single overflow text is discarded and logged |
| MaxRetry | `10` | Max retry attempts |
| ModelParams | | Custom model parameters, e.g., `{"temperature":0.3}` |
| CustomPrompt | `False` | Enable custom system prompts; config file generated at `BepInEx/config/AutoLLM_CustomPrompt.txt` |
| HalfWidth | `True` | Convert full-width characters to half-width |
| DisableSpamChecks | `True` | Disable AutoTranslator spam detection |
| ~~LogLevel~~ | Removed | ~~Log level~~. Controlled by `BepInEx.cfg` |
| ~~Log2File~~ | Removed | ~~Log to file~~. Unified output to `LogOutput.log` |
| ~~Terminology~~ | Removed | ~~Term glossary~~ |
| ~~GameName~~ | Removed | ~~Game name~~ |
| ~~GameDesc~~ | Removed | ~~Game description~~ |
| ~~MaxWordCount~~ | Removed | ~~Max words per batch~~ |
