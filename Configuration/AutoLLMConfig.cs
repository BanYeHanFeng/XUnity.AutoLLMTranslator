using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
// 日志级别过滤交给 BepInEx 的 Console/Disk listener 按 BepInEx.cfg 过滤，本插件不再本地重复门控。


internal class AutoLLMConfig
{
    public string Model { get; set; } = "";
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelParams { get; set; } = "";
    // ParallelCount 已废弃：固定单并发，以保留对话历史与术语表落盘的稳定语义。
    public const int ParallelCount = 1;
    public int MaxContext { get; set; } = 4096;
    public int MaxRetry { get; set; } = 5;
    public bool CustomPrompt { get; set; } = false;
    public bool AutoGlossary { get; set; } = false;
    public bool HalfWidth { get; set; } = true;
    public bool DisableSpamChecks { get; set; } = true;
    public string? BepInExRoot { get; set; }              // FindBepInExRoot 可能返回 null
    public string? SourceLanguage { get; set; }          // 框架提供，可能 null
    public string? DestinationLanguage { get; set; }     // 框架提供，可能 null
    public Dictionary<string, object> ParsedModelParams { get; set; } = new Dictionary<string, object>();
    public string CachedSystemPrompt { get; set; } = null!;  // IsValid=true 时由 PromptManager.Build 设置
    public string CachedGlossaryPrompt { get; set; } = "";   // AutoGlossary=true 时由 PromptManager.BuildGlossaryPrompt 设置（已替换占位符，不含术语表）
    public string? GlossaryPath { get; set; }            // 术语表文件路径，AutoGlossary=true 时由 PromptManager 设置

    /// <summary>实际发起请求的完整端点地址（含 /chat/completions 后缀，由 Url 派生）。</summary>
    public string EndpointUrl { get; private set; } = "";

    /// <summary>配置是否有效（Model 和 URL 均已填写）。</summary>
    public bool IsValid => !string.IsNullOrEmpty(Model) && !string.IsNullOrEmpty(Url);

    /// <summary>从 IInitializationContext 读取全部配置并初始化。</summary>
    public static AutoLLMConfig FromInitializationContext(IInitializationContext context)
    {
        var config = new AutoLLMConfig();

        // 1. 读取所有配置（顺序与 README.md「全部配置」一致；string 类型加 ?? "" 防御，防止框架返回 null）
        config.Model = context.GetOrCreateSetting("AutoLLM", "Model", "") ?? "";
        config.Url = context.GetOrCreateSetting("AutoLLM", "URL", "") ?? "";
        config.ApiKey = context.GetOrCreateSetting("AutoLLM", "APIKey", "") ?? "";
        config.ModelParams = context.GetOrCreateSetting("AutoLLM", "ModelParams", "") ?? "";
        // ParallelCount 已废弃：不再读取配置，固定为 1（见常量定义）。
        config.MaxContext = context.GetOrCreateSetting("AutoLLM", "MaxContext", 4096);
        config.MaxRetry = context.GetOrCreateSetting("AutoLLM", "MaxRetry", 5);
        config.CustomPrompt = context.GetOrCreateSetting("AutoLLM", "CustomPrompt", false);
        config.AutoGlossary = context.GetOrCreateSetting("AutoLLM", "AutoGlossary", false);
        config.HalfWidth = context.GetOrCreateSetting("AutoLLM", "HalfWidth", true);
        config.DisableSpamChecks = context.GetOrCreateSetting("AutoLLM", "DisableSpamChecks", true);

        // 2. 预解析 ModelParams
        if (!string.IsNullOrEmpty(config.ModelParams))
            config.ParsedModelParams = SimpleJson.ParseModelParams(config.ModelParams);
        else
            config.ParsedModelParams = new Dictionary<string, object>();

        // 3. 定位 BepInEx 根目录
        config.BepInExRoot = FindBepInExRoot(context.TranslatorDirectory);

        // 4. DisableSpamChecks
        if (config.DisableSpamChecks)
            context.DisableSpamChecks();

        // 5. ServicePointManager 配置
        ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, AutoLLMConfig.ParallelCount * 2);
        ServicePointManager.Expect100Continue = false;

        // 6. URL 自动补尾：保留用户填写的 Url 原值（用于日志展示/排查），
        //    派生 EndpointUrl 供 LlmClient 实际请求使用（两种结尾互斥，用 else if 表达语义）。
        config.EndpointUrl = config.Url;
        if (config.EndpointUrl.EndsWith("/v1"))
            config.EndpointUrl += "/chat/completions";
        else if (config.EndpointUrl.EndsWith("/v1/"))
            config.EndpointUrl += "chat/completions";

        // 7. 语言
        config.SourceLanguage = context.SourceLanguage;
        config.DestinationLanguage = context.DestinationLanguage;

        // 8. 验证：与框架约定一致，必填项缺失时抛 EndpointInitializationException，
        //    由 TranslationManager 统一捕获并标记端点初始化失败。
        if (!config.IsValid)
            throw new EndpointInitializationException(
                "AutoLLM 端点需要 Model 与 URL 均已配置（当前未提供）。");

        // 9. 构建并缓存系统提示词
        config.CachedSystemPrompt = PromptManager.Build(config);

        // 10. 构建术语表提示词（仅 AutoGlossary=true 时生效，否则留空）
        if (config.AutoGlossary)
            config.CachedGlossaryPrompt = PromptManager.BuildGlossaryPrompt(config);

        return config;
    }

    /// <summary>
    /// 从 TranslatorDirectory 向上查找 BepInEx 根目录：
    /// 含 core/ 子目录 或 目录名为 BepInEx。
    /// </summary>
    private static string? FindBepInExRoot(string? translatorDir)
    {
        if (string.IsNullOrEmpty(translatorDir)) return null;
        var dir = translatorDir;
        for (int i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir!, "core"))) break;
            if ((Path.GetFileName(dir) ?? "").Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) break;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir || string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }
        return dir;
    }
}
