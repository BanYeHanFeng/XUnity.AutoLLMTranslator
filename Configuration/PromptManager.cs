using System;
using System.IO;
using System.Text;


internal static class PromptManager
{
    /// <summary>
    /// 自定义提示词文件中的分节分隔符。
    /// 文件被此分隔符划分为两节：
    ///   - 分隔符之前：普通模式（AutoGlossary=false 时使用）
    ///   - 分隔符之后：术语表模式（AutoGlossary=true 时使用，应含 {{GLOSSARY}} 占位符）
    /// 首次创建模板文件时，分隔符两侧分别写入 Prompt.Default 与 Prompt.Glossary。
    /// </summary>
    public const string SectionSeparator =
        "# ===== AutoLLM.AutoGlossary=true 以下为术语表模式提示词（其上为普通模式） =====";

    /// <summary>自定义提示词单一文件名。</summary>
    private const string CustomPromptFileName = "AutoLLM_CustomPrompt.txt";

    /// <summary>术语表数据文件名。</summary>
    private const string GlossaryFileName = "AutoLLM_Glossary.txt";

    /// <summary>
    /// 返回已替换 {{SOURCE_LAN}} 和 {{TARGET_LAN}} 的【普通模式】系统提示词（不含 {{GLOSSARY}} 占位符）。
    /// 行尾归一化为 LF。config.CachedSystemPrompt 应存储此返回值。
    /// CustomPrompt=false 时直接使用 Prompt.Default。
    /// </summary>
    public static string Build(AutoLLMConfig config)
    {
        string basePrompt = LoadPromptSection(config, wantGlossary: false, defaultPrompt: Prompt.Default);

        basePrompt = NormalizeLineEndings(basePrompt);
        Logger.Info("系统提示词: " + basePrompt.Length + " 字符");

        return basePrompt
            .Replace("{{SOURCE_LAN}}", config.SourceLanguage ?? "")
            .Replace("{{TARGET_LAN}}", config.DestinationLanguage ?? "");
    }

    /// <summary>
    /// 返回已替换 {{SOURCE_LAN}} 和 {{TARGET_LAN}} 的【术语表模式】系统提示词模板。
    /// 保留 {{GLOSSARY}} 占位符，由 GlossaryManager 在运行时填充。
    /// 仅在 config.AutoGlossary=true 时调用；同时设置 config.GlossaryPath。
    /// CustomPrompt=false 时直接使用 Prompt.Glossary。
    /// </summary>
    public static string BuildGlossaryPrompt(AutoLLMConfig config)
    {
        string basePrompt = LoadPromptSection(config, wantGlossary: true, defaultPrompt: Prompt.Glossary);

        basePrompt = NormalizeLineEndings(basePrompt);
        Logger.Info("术语表提示词: " + basePrompt.Length + " 字符");

        // 设置术语表文件路径（供 GlossaryManager 使用）
        if (!string.IsNullOrEmpty(config.BepInExRoot))
        {
            config.GlossaryPath = Path.Combine(
                Path.Combine(config.BepInExRoot!, "config"), GlossaryFileName);
        }

        return basePrompt
            .Replace("{{SOURCE_LAN}}", config.SourceLanguage ?? "")
            .Replace("{{TARGET_LAN}}", config.DestinationLanguage ?? "");
    }

    /// <summary>
    /// 从单一自定义提示词文件加载并按 wantGlossary 选取对应分节。
    /// 行为：
    ///   - CustomPrompt=false：直接用 defaultPrompt（不读文件）
    ///   - CustomPrompt=true：读 AutoLLM_CustomPrompt.txt
    ///       * 文件不存在：以 Prompt.Default + 分隔符 + Prompt.Glossary 顺序写入后，
    ///         按 wantGlossary 返回对应默认值
    ///       * 文件存在：
    ///         - 含分隔符 → 按 wantGlossary 返回对应分节（分节为空回退到对应 Default/Glossary）
    ///         - 不含分隔符（兼容旧单节文件）→ 整文件作普通模式提示词；术语表模式回退 Prompt.Glossary
    ///   - BepInExRoot 缺失：回退 defaultPrompt
    /// </summary>
    private static string LoadPromptSection(AutoLLMConfig config, bool wantGlossary, string defaultPrompt)
    {
        if (!config.CustomPrompt)
        {
            Logger.Info("使用默认" + (wantGlossary ? "术语表" : "") + "系统提示词");
            return defaultPrompt;
        }

        if (string.IsNullOrEmpty(config.BepInExRoot))
        {
            // BepInEx 根目录定位失败时 Path.Combine 会抛 ArgumentNullException，
            // 此处显式回退到默认提示词，避免端点静默禁用
            Logger.Warn("BepInEx 根目录未定位到，自定义系统提示词不可用，回退默认");
            return defaultPrompt;
        }

        var path = Path.Combine(Path.Combine(config.BepInExRoot!, "config"), CustomPromptFileName);

        if (!File.Exists(path))
        {
            // 首次运行：生成含两套分节的模板文件，方便用户修改
            try
            {
                Directory.CreateDirectory(Path.Combine(config.BepInExRoot!, "config"));
                var template = Prompt.Default + "\n\n" + SectionSeparator + "\n\n" + Prompt.Glossary;
                File.WriteAllText(path, template, Encoding.UTF8);
                Logger.Info("已创建自定义系统提示词模板（含普通/术语表两节）: " + path);
            }
            catch (Exception ex)
            {
                Logger.Error("创建自定义系统提示词模板失败", ex);
            }
            // 不论写入是否成功，首次都返回默认提示词
            return defaultPrompt;
        }

        // 文件已存在：读取并分节
        try
        {
            var content = File.ReadAllText(path, Encoding.UTF8);
            Logger.Info("已加载自定义系统提示词: " + path);

            int sepIdx = content.IndexOf(SectionSeparator, StringComparison.Ordinal);
            if (sepIdx < 0)
            {
                // 兼容旧版本单节文件：没有分隔符
                if (wantGlossary)
                {
                    Logger.Warn("自定义提示词文件缺少分节标记（\"" + SectionSeparator + "\"），" +
                        "术语表模式无法定位术语表分节，回退到内建术语表默认提示词。" +
                        "如需自定义，请按模板格式补充分节（普通模式段在上，术语表模式段在下）。");
                    return Prompt.Glossary;
                }
                // 普通模式：整文件作提示词
                return content;
            }

            // 含分隔符：分为普通模式段（前）与术语表模式段（后）
            string defaultPart = content.Substring(0, sepIdx);
            // 跳过分隔符行：分隔符后通常还有换行，一并去除首部空白/换行
            string glossaryPart = content.Substring(sepIdx + SectionSeparator.Length);
            glossaryPart = glossaryPart.TrimStart('\r', '\n');

            if (wantGlossary)
            {
                if (string.IsNullOrEmpty(glossaryPart) || glossaryPart.Trim().Length == 0)
                {
                    Logger.Warn("自定义提示词文件术语表分节为空，回退到内建术语表默认提示词");
                    return Prompt.Glossary;
                }
                return glossaryPart;
            }
            else
            {
                if (string.IsNullOrEmpty(defaultPart) || defaultPart.Trim().Length == 0)
                {
                    Logger.Warn("自定义提示词文件普通模式分节为空，回退到内建默认提示词");
                    return Prompt.Default;
                }
                return defaultPart;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取自定义系统提示词失败", ex);
            return defaultPrompt;
        }
    }

    /// <summary>将 CRLF 和孤立 CR 统一为 LF。先处理 CRLF 再处理 CR，避免重复换行。</summary>
    private static string NormalizeLineEndings(string s)
    {
        return s.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}