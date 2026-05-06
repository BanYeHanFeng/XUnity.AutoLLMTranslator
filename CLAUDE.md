# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Development

```bash
# Build
dotnet build XUnity.AutoLLMTranslator.sln -c Release

# Build (CI, no post-build ILRepack/XCOPY)
dotnet build XUnity.AutoLLMTranslator.sln -c Release "/p:GameDir=$env:temp\GameOutput\"
```

- **Target**: `net35` (.NET Framework 3.5, Unity compatibility)
- **LangVersion**: latest (C# features allowed as long as they target net35)
- **Dependencies**: local DLLs in `packages/` (not NuGet) — `dotnet restore` only needed in CI
- **Post-build** (local only): ILRepack merges referenced DLLs into the output assembly, then XCOPY to `$(GameDir)`
- **CI**: GitHub Actions on `windows-latest`, .NET 8.x SDK. Creates GitHub release from `bin/Release/net35/XUnity.AutoLLMTranslator.dll` on `v*` tags
- **ImplicitUsings is disabled** — all `using` statements are explicit
- **No Newtonsoft.Json** — use `SimpleJson.cs` for all JSON serialization

## Key Internal Types (SimpleJson.cs)

| Method | Purpose |
|---|---|
| `Serialize(object)` | Serializes any object (strings, dicts, lists, anonymous types) |
| `SerializeTexts(string[])` | Wraps texts in `{"texts": [...]}` format |
| `ParseTexts(string)` | Parses `{"texts": [...]}` back into `string[]` |
| `ParseSseContent(string)` | Extracts `choices[0].delta.content` from SSE stream chunks |
| `ParseSseUsage(string)` | Extracts `usage` object from SSE stream chunks (for token/cache stats) |
| `ParseModelParams(string)` | Parses JSON into `Dictionary<string, object>` for ModelParams config |
| `ParseJsonObject(string)` | Parses any JSON string into `Dictionary<string, object>` |

## Architecture

| File | Role |
|---|---|
| `AutoLLMTranslatorEndpoint.cs` | Framework glue. Registers `LLMTranslatorEndpoint` (`Endpoint=AutoLLMTranslate`). Extends `WwwEndpoint` with `MaxTranslationsPerRequest=1`, `MaxConcurrency=500` |
| `TranslatorTask.cs` | Orchestrator: HTTP listener, task queue, batch scheduler, result mapping, retry logic |
| `LlmClient.cs` | Stateless HTTP client: builds request, sends to LLM API, parses SSE stream, extracts usage stats |
| `ConversationHistory.cs` | Manages multi-turn conversation history: message building, MaxContext trimming |
| `Config.cs` | System prompt template with `{{SOURCE_LAN}}` / `{{TARGET_LAN}}` placeholders |
| `SimpleJson.cs` | Minimal JSON serializer/parser |
| `Logger.cs` | Logging wrapper around `XuaLogger.Common` |

## How It Works

1. Framework calls `OnCreateRequest` → serializes text → POST to `127.0.0.1:20000`
2. `HttpListener` receives → `AddTask` → joins task queue
3. `Polling()` (50ms interval) monitors queue; dispatches when `MaxWordCount` exceeded or `BatchTimeout` ms of inactivity
4. `ProcessTaskBatch` builds messages via `ConversationHistory.BuildMessages`, calls `LlmClient.Translate`
5. SSE stream parsed by `LlmClient`; accumulated JSON parsed as `{"1":"trans1","2":"trans2"}`
6. Results mapped back to tasks; failed tasks retried up to `MaxRetry` times
7. Successful exchange appended to `ConversationHistory` via `AppendExchange`

## Configuration

| Parameter | Default | Purpose |
|---|---|---|
| `Model` | — | Model name (must support JSON Output) |
| `URL` | — | API endpoint (`/v1` expanded to `/v1/chat/completions`) |
| `APIKey` | — | API key |
| `BatchTimeout` | 400 | Max ms to wait for new texts before dispatching |
| `MaxWordCount` | 2500 | Max chars per batch, triggers immediate dispatch |
| `ParallelCount` | 1 | Concurrent LLM requests. >1 auto-disables conversation history |
| `MaxContext` | 0 | Model context limit (tokens). History cleared if exceeded. `0`=no limit |
| `MaxRetry` | 10 | Max retries for failed translations |
| `ModelParams` | — | Extra JSON params merged into request body |
| `ExtraPrompt` | — | Appended to system prompt (use for terminology, style) |
| `HalfWidth` | True | Convert full-width symbols to half-width |
| `DisableSpamChecks` | True | Disable XUnity spam detection |

## Important Conventions

- **No tests in this repo**
- **Messages use `Dictionary<string, object>`** (not anonymous types) to avoid reflection in SimpleJson serialization
- **`HalfWidthRegex` is `static readonly`** with `RegexOptions.Compiled`
- **Conversation history disabled** when `ParallelCount > 1` (shared list would interleave)
- **`Prompt_cache_hit_tokens`** fields may not appear in streaming responses from non-DeepSeek APIs
- **`.csproj` uses SDK-style** — all `*.cs` files auto-included
