<p align="center">
  <a href="README.md">简体中文</a> |
  <a href="README.en.md">English</a>
</p>

## Overview
- **Built for personal needs, bug fixes are welcome — please submit an issue**

## Acknowledgments
- [bbepis/XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) — **Plugin foundation**
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) — **Upstream repository**

## Key Changes from Upstream
- **JSON Output Mode**: Requires LLM to output translations in JSON format, combined with streaming incremental parsing to prevent format-related failures
- **Conversation History & Cache Reuse**: Multiple batches share context, leveraging LLM caching to reduce costs; history auto-clears when exceeding the limit
- **Token Usage Statistics**: Real-time display of input/output token consumption and cache hit/miss per batch
- **Custom System Prompt**: Load fully customized translation style and rules from a local JSON file; auto-generates default template on first enable
- **Rate Limit Backoff**: Automatically waits and retries on API rate limits with exponential backoff, without consuming retry attempts
- **Batch Merging**: Multiple short texts are automatically merged into a single translation round
- **Event-Driven Scheduling**: Immediate response to new tasks instead of fixed-interval polling, reducing latency and idle overhead
- **Port Auto-Retry**: Internal service automatically tries the next port on conflict, preventing startup failures
- **Log Level Control**: Log levels managed by the BepInEx unified configuration file
- **Streamlined Configuration**: Removed unused parameters such as term glossary, game name/description

## Quick Start
After installing the plugin via BepInEx as described in the [upstream repository](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator), run the game once to auto-generate the `[AutoLLM]` config section. Fill in the following three items to get started:

```ini
[AutoLLM]
Model=model-name
URL=api-url
APIKey=api-key
```

## All Configuration
| Parameter | Description | Default | Notes |
|---|---|---|---|
| Model | Model name | (none) | Model must support JSON output |
| URL | API endpoint | (none) | |
| APIKey | API key | (none) | |
| MaxWordCount | Max characters per batch | `2500` | New batch starts after exceeding this limit |
| ParallelCount | Concurrent requests | `1` | >1 disables conversation history; batches are queued and merged when fully occupied |
| MaxContext | Max context (tokens) | `1024` | Clears conversation history when exceeded; recommended ≤15000 |
| MaxRetry | Max retry attempts | `10` | |
| ModelParams | Extra model parameters (JSON) | (none) | e.g. `{"temperature":0.3}` |
| CustomPrompt | Custom system prompt | `False` | When enabled, config file is at `BepInEx/config/AutoLLM_CustomPrompt.txt` |
| HalfWidth | Full-width to half-width conversion | `True` | |
| DisableSpamChecks | Disable XUnity spam detection | `True` | Recommended to minimize false-positives |
| ~~LogLevel~~ | ~~Log level~~ | — | Removed, controlled by `BepInEx.cfg` |
| ~~Log2File~~ | ~~Log output to file~~ | — | Removed, unified output to `LogOutput.log` |
| ~~Terminology~~ | ~~Term glossary~~ | — | Removed |
| ~~ExtraPrompt~~ | ~~Additional prompt~~ | — | Removed, replaced by `CustomPrompt` |
| ~~GameName~~ | ~~Game name~~ | — | Removed |
| ~~GameDesc~~ | ~~Game description~~ | — | Removed |
