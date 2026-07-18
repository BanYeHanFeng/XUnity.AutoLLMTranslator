<p align="center">
  <a href="README.md">简体中文</a> |
  <a href="README.en.md">English</a>
</p>

## Introduction
- **Personal needs are now mostly met. If you encounter any issues, please submit an issue — I will respond and resolve them.**

## Acknowledgments
- [bbepis/XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) **Plugin foundation**
- [NothingNullNull/XUnity.AutoLLMTranslator](https://github.com/NothingNullNull/XUnity.AutoLLMTranslator) **Upstream repository**

## Major Changes Compared to Upstream
**Text**
- Event-driven wake-up for ultra-low latency text acquisition
- Text is only sent to the model after 100ms of no new text, mitigating text fragmentation

**Model**
- Set 100% JSON output parameters to resolve model occasionally outputting incorrect formats (requires model support)
- Conversation history replaces translation history, reducing historical translation costs

**Configuration**
- Removed `LogLevel` `Log2File` `Terminology` `GameName` `GameDesc` `MaxWordCount` `Requirement` `Interval` `ParallelCount`
- Removed multi-key load balancing; `APIKey` no longer supports `;`-separated round-robin
- Log level is now managed uniformly by `BepInEx.cfg`, output to `LogOutput.log`
- Added `MaxContext` parameter for custom maximum context length
- Added `CustomPrompt` parameter for fully customizable system prompts
- Added `AutoGlossary` parameter — model outputs terms alongside translations
- Streamlined default prompt (2947 chars → 171 chars (normal mode) / 273 chars (glossary mode))

**Other**
- Removed HTTP proxy layer, reducing overhead

## FAQ
**Q: How do I install this plugin?**
<p>
  <b>- A:</b> <a href="docs/安装.en.md">Installation Guide</a><br>
  <b>- Note:</b> This plugin currently does not support IL2CPP games; adaptation may come in the future.
</p>

**Q: Some fonts show as □□□**
<p>
  <b>- A:</b> <a href="docs/更换字体.en.md">Solution</a>
</p>

**Q: Model outputs unstable character names — how to fix?**
<p>
  <b>- A:</b> <a href="docs/术语表.en.md">Auto Glossary</a><br>
  <b>- Note:</b> Enabling auto glossary adds about ten-plus seconds of response time — it's a trade-off.
</p>

**Q: The model has thinking enabled by default, but thinking is too slow — how to disable?**
<p>
  <b>- A:</b> <a href="docs/关闭思考.en.md">How to disable</a><br>
  <b>- Note:</b> Disabling thinking will affect translation quality and auto glossary quality, but will provide faster responses.
</p>

**Q: Which model provider do you recommend?**
<p>
  <b>- A:</b> DeepSeek v4 flash<br>
  <b>- Note:</b> The developer has only used glm 5.2 and DeepSeek v4 series so far. All translation testing was done with DeepSeek v4 flash.
</p>

**Q: How to deploy a local model?**
<p>
  <b>- A:</b> Please search for tutorials on <code>Bilibili</code>, then set the <code>MaxContext</code> parameter according to your configured context.<br>
  <b>- Note:</b> Incorrect parameter settings will cause translation failures.
</p>

## All Configuration Options
| Parameter | Default | Description |
|---|---|---|
| Model | | Model name |
| URL | | API endpoint. If suffixed with `/v1` or `/v1/`, it is auto-completed to `/v1/chat/completions` |
| APIKey | | API key |
| ModelParams | | Custom model parameters, e.g., `{"temperature":0.3}` |
| MaxContext | `4096` | Maximum context token count. Automatically estimates token consumption per text (calibrated after receiving API response; otherwise estimated at ~0.75 token per character). When exceeded, three scenarios apply: ① Clear conversation history ② Overflow distributed to next batch ③ If a single text exceeds, it is discarded and logged |
| MaxRetry | `5` | Maximum retry attempts |
| CustomPrompt | `False` | Whether to enable custom prompts. When enabled, the config file is generated at `GameRoot/BepInEx/config/AutoLLM_CustomPrompt.txt`. There are two sets of prompts: content under `[普通模式提示词]` is the normal system prompt, and content under `[自动术语表模式提示词]` is the system prompt used when auto glossary is enabled |
| AutoGlossary | `False` | Whether to enable auto glossary. When enabled, the glossary file is generated at `GameRoot/BepInEx/config/AutoLLM_Glossary.json`. ① The model parses new terms alongside translations ② Glossary is injected via placeholder in the system prompt, only injected when history is empty |
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
| ~~ParallelCount~~ | Removed | ~~Parallel translation count~~ |
