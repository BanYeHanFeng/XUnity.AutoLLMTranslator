# XUnity.AutoLLMTranslator — Agent Guide

## Project

C# .NET Framework plugin for [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator). Translates game text via LLM APIs.

- **Target**: `net35` (compatible with .NET 3.5+, Unity games)
- **LangVersion**: latest (C# features allowed, but must target net35)
- **Single project** — one `.csproj`, one `.sln`

## Build

```bash
dotnet restore XUnity.AutoLLMTranslator.sln
dotnet build XUnity.AutoLLMTranslator.sln -c Release
```

- Dependencies are **local DLLs** in `packages/` (not NuGet). No `dotnet restore` actually needed.
- Post-build: ILRepack merges dependencies into the output assembly, then XCOPY to `GameDir` (configured in `.csproj`).
- CI: runs on `windows-latest`, .NET 8.x SDK. Creates GitHub release from `bin/Release/net35/XUnity.AutoLLMTranslator.dll` on `v*` tags.

## Key Files

| File | Role |
|---|---|
| `AutoLLMTranslatorEndpoint.cs` | Entrypoint — `LLMTranslatorEndpoint` class, registers as `AutoLLMTranslate` endpoint |
| `TranslatorTask.cs` | Core logic: HTTP server (port 20000), batching, retry, LLM API calls |
| `Config.cs` | LLM prompt template (static class) |
| `TranslateDB.cs` | Translation cache/database |
| `FuzzyString/` | In-source fuzzy string matching library |
| `SimpleJson.cs` | Minimal JSON serializer (no Newtonsoft.Json dependency) |

## Architecture

- **Endpoint ID**: `AutoLLMTranslate` — set `Endpoint=AutoLLMTranslate` in `Config.ini`
- Runs a local `HttpListener` on `http://127.0.0.1:20000/` — the plugin sends texts to itself via HTTP
- `MaxTranslationsPerRequest = 1`, `MaxConcurrency = 100` — batching is handled internally by `TranslatorTask`, not by the `WwwEndpoint` framework
- Retry, parallelism, context window, and terminology are configured via `[AutoLLM]` section in `Config.ini`

## Important Conventions

- **No tests** in this repo.
- **ImplicitUsings is disabled** — all `using` statements are explicit.
- **Do NOT add Newtonsoft.Json dependency** — `SimpleJson.cs` is the JSON serializer.
- Prompt uses `{{PLACEHOLDER}}` template variables (`SOURCE_LAN`, `TARGET_LAN`, `OTHER`, `GAMENAME`, `GAMEDESC`, `HISTORY`, `RECENT`).
- Terminology format in config: `Lorien==罗林|Skadi==斯卡蒂` (`==` between original and translation, `|` between entries).
- API keys separated by `;` for load balancing.
