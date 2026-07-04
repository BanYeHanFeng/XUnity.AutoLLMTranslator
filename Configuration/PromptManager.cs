using System;
using System.IO;
using System.Text;


internal static class PromptManager
{
    /// <summary>
    /// 返回已替换 {{SOURCE_LAN}} 和 {{TARGET_LAN}} 的系统提示词（行尾已归一化为 LF）。
    /// config.CachedSystemPrompt 应存储此返回值。
    /// </summary>
    public static string Build(AutoLLMConfig config)
    {
        string basePrompt = LoadPromptFile(config, customPrompt: config.CustomPrompt,
            defaultPrompt: Prompt.Default, customFileName: "AutoLLM_CustomPrompt.txt",
            label: "系统提示词");

        // 统一行尾为 LF：避免 Windows(CRLF)/Linux(LF) 构建产物字符串长度漂移，
        // 同时使 token 估算（chars×0.75）跨平台一致。
        // 必须在日志输出长度之前完成，否则日志显示的字符数与实际字符串不一致。
        basePrompt = NormalizeLineEndings(basePrompt);
        Logger.Info("系统提示词: " + basePrompt.Length + " 字符");

        return basePrompt
            .Replace("{{SOURCE_LAN}}", config.SourceLanguage ?? "")
            .Replace("{{TARGET_LAN}}", config.DestinationLanguage ?? "");
    }

    /// <summary>
    /// 构建术语表模式系统提示词（已替换语言占位符，保留 {{GLOSSARY}} 占位符待运行时填充）。
    /// 仅在 config.AutoGlossary=true 时调用。同时设置 config.GlossaryPath。
    /// </summary>
    public static string BuildGlossaryPrompt(AutoLLMConfig config)
    {
        // 自定义术语表提示词使用独立文件 AutoLLM_CustomGlossaryPrompt.txt
        // CustomPrompt 开关不影响术语表提示词，两者独立控制
        string basePrompt = LoadPromptFile(config, customPrompt: true,
            defaultPrompt: Prompt.Glossary, customFileName: "AutoLLM_CustomGlossaryPrompt.txt",
            label: "术语表提示词");

        basePrompt = NormalizeLineEndings(basePrompt);
        Logger.Info("术语表提示词: " + basePrompt.Length + " 字符");

        // 设置术语表文件路径（供 GlossaryManager 使用）
        if (!string.IsNullOrEmpty(config.BepInExRoot))
        {
            config.GlossaryPath = Path.Combine(
                Path.Combine(config.BepInExRoot!, "config"), "AutoLLM_Glossary.txt");
        }

        return basePrompt
            .Replace("{{SOURCE_LAN}}", config.SourceLanguage ?? "")
            .Replace("{{TARGET_LAN}}", config.DestinationLanguage ?? "");
    }

    /// <summary>
    /// 通用提示词文件加载逻辑。
    /// customPrompt=false 时直接用默认提示词；true 时尝试读取自定义文件，不存在则创建模板。
    /// </summary>
    private static string LoadPromptFile(AutoLLMConfig config, bool customPrompt,
        string defaultPrompt, string customFileName, string label)
    {
        if (!customPrompt)
        {
            Logger.Info("使用默认" + label);
            return defaultPrompt;
        }

        if (string.IsNullOrEmpty(config.BepInExRoot))
        {
            // BepInEx 根目录定位失败时 Path.Combine 会抛 ArgumentNullException，
            // 此处显式回退到默认提示词，避免端点静默禁用
            Logger.Warn("BepInEx 根目录未定位到，自定义" + label + "不可用，回退默认");
            return defaultPrompt;
        }

        var path = Path.Combine(Path.Combine(config.BepInExRoot!, "config"), customFileName);
        if (File.Exists(path))
        {
            try
            {
                var content = File.ReadAllText(path, Encoding.UTF8);
                Logger.Info("已加载自定义" + label + ": " + path);
                return content;
            }
            catch (Exception ex)
            {
                Logger.Error("读取自定义" + label + "失败", ex);
                return defaultPrompt;
            }
        }
        else
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(config.BepInExRoot!, "config"));
                File.WriteAllText(path, defaultPrompt, Encoding.UTF8);
                Logger.Info("已创建默认自定义" + label + "模板: " + path);
                return defaultPrompt;
            }
            catch (Exception ex)
            {
                Logger.Error("创建自定义" + label + "模板失败", ex);
                return defaultPrompt;
            }
        }
    }

    /// <summary>将 CRLF 和孤立 CR 统一为 LF。先处理 CRLF 再处理 CR，避免重复换行。</summary>
    private static string NormalizeLineEndings(string s)
    {
        return s.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
