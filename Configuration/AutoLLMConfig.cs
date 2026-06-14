#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;


internal class AutoLLMConfig
{
    public string Model { get; set; } = "";
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int ParallelCount { get; set; } = 1;
    public int MaxRetry { get; set; } = 10;
    public int MaxContext { get; set; } = 4096;
    public string ModelParams { get; set; } = "";
    public bool CustomPrompt { get; set; } = false;
    public bool HalfWidth { get; set; } = true;
    public bool DisableSpamChecks { get; set; } = true;
    public string BepInExRoot { get; set; }
    public string SourceLanguage { get; set; }
    public string DestinationLanguage { get; set; }
    public Dictionary<string, object> ParsedModelParams { get; set; }
    public string CachedSystemPrompt { get; set; }

    // 日志等级
    public bool InfoEnabled { get; set; } = true;
    public bool WarnEnabled { get; set; } = true;
    public bool DebugEnabled { get; set; } = false;

    /// <summary>配置是否有效（Model 和 URL 均已填写）。</summary>
    public bool IsValid => !string.IsNullOrEmpty(Model) && !string.IsNullOrEmpty(Url);

    /// <summary>从 IInitializationContext 读取全部配置并初始化。</summary>
    public static AutoLLMConfig FromInitializationContext(IInitializationContext context)
    {
        var config = new AutoLLMConfig();

        // 1. 读取所有配置（string 类型加 ?? "" 防御，防止框架返回 null）
        config.Model = context.GetOrCreateSetting("AutoLLM", "Model", "") ?? "";
        config.Url = context.GetOrCreateSetting("AutoLLM", "URL", "") ?? "";
        config.ApiKey = context.GetOrCreateSetting("AutoLLM", "APIKey", "") ?? "";
        config.ParallelCount = context.GetOrCreateSetting("AutoLLM", "ParallelCount", 1);
        config.MaxRetry = context.GetOrCreateSetting("AutoLLM", "MaxRetry", 10);
        config.MaxContext = context.GetOrCreateSetting("AutoLLM", "MaxContext", 4096);
        config.ModelParams = context.GetOrCreateSetting("AutoLLM", "ModelParams", "") ?? "";
        config.CustomPrompt = context.GetOrCreateSetting("AutoLLM", "CustomPrompt", false);
        config.HalfWidth = context.GetOrCreateSetting("AutoLLM", "HalfWidth", true);
        config.DisableSpamChecks = context.GetOrCreateSetting("AutoLLM", "DisableSpamChecks", true);

        // 2. 预解析 ModelParams
        if (!string.IsNullOrEmpty(config.ModelParams))
            config.ParsedModelParams = SimpleJson.ParseModelParams(config.ModelParams);
        else
            config.ParsedModelParams = new Dictionary<string, object>();

        // 3. 定位 BepInEx 根目录
        config.BepInExRoot = FindBepInExRoot(context.TranslatorDirectory);

        // 4. 读取日志等级
        ParseLogLevels(config.BepInExRoot, config);

        // 5. DisableSpamChecks
        if (config.DisableSpamChecks)
            context.DisableSpamChecks();

        // 6. ServicePointManager 配置
        ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, config.ParallelCount * 2);
        ServicePointManager.Expect100Continue = false;

        // 7. URL 自动补尾
        if (config.Url.EndsWith("/v1"))
            config.Url += "/chat/completions";
        if (config.Url.EndsWith("/v1/"))
            config.Url += "chat/completions";

        // 8. 语言
        config.SourceLanguage = context.SourceLanguage;
        config.DestinationLanguage = context.DestinationLanguage;

        // 9. 验证
        if (!config.IsValid)
            return config;

        // 10. ThreadPool 扩容
        int minWorker, minIo;
        ThreadPool.GetMinThreads(out minWorker, out minIo);
        int needed = config.ParallelCount + 2;
        if (minWorker < needed || minIo < needed)
            ThreadPool.SetMinThreads(Math.Max(minWorker, needed), Math.Max(minIo, needed));

        // 11. 构建并缓存系统提示词
        config.CachedSystemPrompt = PromptManager.Build(config);

        return config;
    }

    /// <summary>
    /// 从 TranslatorDirectory 向上查找 BepInEx 根目录：
    /// 含 core/ 子目录 或 目录名为 BepInEx。
    /// </summary>
    private static string FindBepInExRoot(string translatorDir)
    {
        if (string.IsNullOrEmpty(translatorDir)) return null;
        var dir = translatorDir;
        for (int i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "core"))) break;
            if ((Path.GetFileName(dir) ?? "").Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) break;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir || string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }
        return dir;
    }

    /// <summary>
    /// 从 BepInEx/config/BepInEx.cfg 读取日志等级（与原始 Logger.Init 逻辑一致）。
    /// </summary>
    private static void ParseLogLevels(string bepinExRoot, AutoLLMConfig config)
    {
        try
        {
            var cfgPath = Path.Combine(Path.Combine(bepinExRoot, "config"), "BepInEx.cfg");
            if (!File.Exists(cfgPath)) return;

            var sections = ParseIniFile(cfgPath);

            // [Logging.Console]
            bool cEnabled = false, cDebug = false, cInfo = false, cWarn = false;
            if (sections.TryGetValue("Logging.Console", out var cSec))
            {
                cEnabled = GetBoolValue(cSec, "Enabled", false);
                if (cSec.TryGetValue("LogLevels", out var cLevels))
                {
                    cDebug = ContainsLevel(cLevels, "Debug");
                    cInfo = ContainsLevel(cLevels, "Info");
                    cWarn = ContainsLevel(cLevels, "Warning");
                }
            }

            // [Logging.Disk] — Disk 默认开启
            bool dEnabled = true, dDebug = false, dInfo = false, dWarn = false;
            if (sections.TryGetValue("Logging.Disk", out var dSec))
            {
                dEnabled = GetBoolValue(dSec, "Enabled", true);
                if (dSec.TryGetValue("LogLevels", out var dLevels))
                {
                    dDebug = ContainsLevel(dLevels, "Debug");
                    dInfo = ContainsLevel(dLevels, "Info");
                    dWarn = ContainsLevel(dLevels, "Warning");
                }
            }

            // 综合：任一端开启即生效
            config.DebugEnabled = (cEnabled && cDebug) || (dEnabled && dDebug);
            config.InfoEnabled = (cEnabled && cInfo) || (dEnabled && dInfo);
            config.WarnEnabled = (cEnabled && cWarn) || (dEnabled && dWarn);
        }
        catch { }
    }

    // ---- INI 解析辅助方法（从原始 Logger.cs 迁移） ----

    private static Dictionary<string, Dictionary<string, string>> ParseIniFile(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        string currentSection = null;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed[0] == '#' || trimmed[0] == ';')
                continue;
            if (trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
            {
                currentSection = trimmed.Substring(1, trimmed.Length - 2);
                if (!result.ContainsKey(currentSection))
                    result[currentSection] = new Dictionary<string, string>();
                continue;
            }
            if (currentSection != null)
            {
                int eq = trimmed.IndexOf('=');
                if (eq > 0)
                {
                    var key = trimmed.Substring(0, eq).Trim();
                    var val = trimmed.Substring(eq + 1).Trim();
                    result[currentSection][key] = val;
                }
            }
        }
        return result;
    }

    private static bool ContainsLevel(string levels, string levelName)
    {
        if (string.IsNullOrEmpty(levels)) return false;
        foreach (var part in levels.Split(','))
        {
            var t = part.Trim();
            if (t.Equals("All", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals(levelName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool GetBoolValue(Dictionary<string, string> section, string key, bool defaultValue)
    {
        if (section.TryGetValue(key, out var value))
        {
            if (bool.TryParse(value, out var result)) return result;
            if (value == "1") return true;
            if (value == "0") return false;
        }
        return defaultValue;
    }
}
