# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Development

```bash
# Build
dotnet build XUnity.AutoLLMTranslator.sln -c Release

# Build (CI, no post-build ILRepack/XCOPY)
dotnet build XUnity.AutoLLMTranslator.sln -c Release "/p:GameDir=$env:temp\GameOutput\"

# Single project, no test project exists
```

- **Target**: `net35` (.NET Framework 3.5, Unity compatibility)
- **LangVersion**: latest (C# features allowed as long as they target net35)
- **Dependencies**: local DLLs in `packages/` (not NuGet) — `dotnet restore` only needed in CI
- **Post-build** (local only): ILRepack merges referenced DLLs into the output assembly, then XCOPY to `$(GameDir)`
- **CI**: GitHub Actions on `windows-latest`, .NET 8.x SDK. Creates GitHub release from `bin/Release/net35/XUnity.AutoLLMTranslator.dll` on `v*` tags
- **ImplicitUsings is disabled** — all `using` statements are explicit
- **No Newtonsoft.Json** — use `SimpleJson.cs` for all JSON serialization
- **AGENTS.md is a legacy file** — its contents have been merged into this CLAUDE.md; prefer this file as the canonical reference

## Key Internal Types (SimpleJson.cs)

`SimpleJson` is a custom JSON serializer/parser (no NuGet dependency). Key methods:

| Method | Purpose |
|---|---|
| `Serialize(object)` | Serializes any object (strings, dicts, lists, anonymous types) |
| `SerializeTexts(string[])` | Wraps texts in `{"texts": [...]}` format |
| `ParseTexts(string)` | Parses `{"texts": [...]}` back into `string[]` |
| `ParseSseContent(string)` | Extracts `choices[0].delta.content` from SSE stream chunks |
| `ParseModelParams(string)` | Parses JSON into `Dictionary<string, object>` for ModelParams config |

## SSE Streaming & Response Parsing

The LLM API response is consumed as a Server-Sent Events (SSE) stream. Each `data: {...}` line is parsed by `SimpleJson.ParseSseContent()`. The content is accumulated line-by-line, and completed translations are matched via the regex `\[(\d+)\]="(.*?)"`. Key details:

- `thinking` / `response` tags (from DeepSeek-style models) and `<context_think>` XML tags are stripped from the stream before parsing
- Full-width characters are converted to half-width if `HalfWidth=True`
- Each completed translation is immediately returned to the caller (`TaskRespond`) — streaming in-progress
- Failed translations within a batch are retried individually (up to `MaxRetry` times)

## Batching & Polling

- `Polling()` runs on a background thread every `Interval` ms
- Decides to dispatch when: (a) there are waiting tasks AND (b) either `MaxWordCount` is exceeded OR `BatchTimeout` ms have passed since the last new text
- Selects up to `ParallelCount` batches per poll cycle
- Each batch is processed on its own background thread
- Failed tasks with retryCount > 2 are batched alone (not merged with other tasks)

## Architecture

A C# .NET Framework 3.5 plugin for [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) that translates Unity game text via OpenAI-compatible LLM APIs.

### Key Files

| File | Role |
|---|---|
| `AutoLLMTranslatorEndpoint.cs` | Entrypoint. Registers `LLMTranslatorEndpoint` as the `AutoLLMTranslate` endpoint (set `Endpoint=AutoLLMTranslate` in Config.ini). Extends `WwwEndpoint` with `MaxTranslationsPerRequest = 1` and `MaxConcurrency = 500` |
| `TranslatorTask.cs` | Core engine: HTTP server on `127.0.0.1:20000`, batching, retry/backoff, streaming SSE parsing from LLM API |
| `Config.cs` | Static class holding the LLM system prompt template. Uses `{{PLACEHOLDER}}` variables: `SOURCE_LAN`, `TARGET_LAN`, `OTHER`, `GAMENAME`, `GAMEDESC`, `HISTORY`, `RECENT` |
| `TranslateDB.cs` | Translation cache: loads existing translations from text files, provides fuzzy-search lookup via `FuzzyString` library, supports terminology matching via `==` format |
| `SimpleJson.cs` | Minimal JSON serializer/parser — used for both LLM API communication and text array serialization |
| `Logger.cs` | Logging wrapper around `XuaLogger.Common`. Supports file output. Levels: Error, Warning, Info, Debug, Null |

### How It Works

1. **Endpoint registration**: XUnity.AutoTranslator calls `OnCreateRequest` with untranslated game text
2. **Local HTTP hop**: endpoint serializes the text and sends a POST to the local `HttpListener` on port 20000
3. **Batching**: `TranslatorTask` collects texts into batches (up to `MaxWordCount` chars or `BatchTimeout` ms of inactivity), then sends them as a single LLM API call
4. **Streaming SSE**: reads the LLM response via SSE, parses `[N]="translation"` lines incrementally, marks each task complete as its translation arrives
5. **Fuzzy cache**: `TranslateDB` stores translations by source text hash; `Search()` uses fuzzy string comparison from `FuzzyString/` to retrieve relevant historical translations as context for the LLM

### Concurrency Model

- `HttpListener` runs on a background thread
- `Polling()` runs on a background thread, sleeping `Interval` ms between checks
- Each batch spins up a new background thread via `Thread`
- `curProcessingCount` throttles parallelism against `ParallelCount`
- Failed tasks are retried up to `MaxRetry` times with re-queuing

### Configuration (Config.ini `[AutoLLM]` section)

| Parameter | Purpose |
|---|---|
| `URL` | LLM API endpoint (appends `/chat/completions` if ends with `/v1`) |
| `APIKey` | API key(s), `;`-separated for round-robin load balancing |
| `Model` | Model name |
| `Terminology` | Glossary. Format: `Lorien==罗林\|Skadi==斯卡蒂` |
| `MaxWordCount` | Max chars per batch |
| `BatchTimeout` | Max ms to wait before dispatching a partial batch |
| `ParallelCount` | Max concurrent LLM requests |
| `ModelParams` | Extra JSON params merged into the request body |

### Important Conventions

- **No tests in this repo**
- Prompt uses `{{PLACEHOLDER}}` template variables only
- The `FuzzyString/` directory is an in-source copy of the FuzzyString library (not a NuGet package)
- Terminology `==` format with `|` separator between entries
