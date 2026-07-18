using System;
using System.IO;
using System.Text;


internal static class PromptManager
{
    /// <summary>
    /// 默认【普通模式】系统提示词模板。
    /// 含 {{TARGET_LAN}} 占位符，由本类的 Build 方法替换（Build 仍会安全替换不存在的 {{SOURCE_LAN}}）。
    /// 输出结构与解析器一致：{"1":"译文1","2":"译文2"}。
    /// </summary>
    private const string Default = @"我是游戏文本翻译家，原文翻译为{{TARGET_LAN}}，规则:
如无法翻译或缩略词难以理解则输出原文
json输出示例:
输入: {""1"":""「お前はなぜ監視官になった？」"",""2"":""「『レクシスを守るため』だと言っていたな。」""}
输出: {""1"":""「你为何成为了监视官？」"",""2"":""「你曾说过，是『为了保护雷克西斯』吧。」""}";

    /// <summary>
    /// 默认【术语表模式】系统提示词模板。
    /// 要求模型在翻译同时输出新术语，输出结构为 {"1":"译文","glossary":{...}}（译文平铺于顶层，
    /// 与 BatchResponseParser 按 UserKey 直接取译文、单独取 glossary 的解析逻辑一致）。
    /// 术语表占位符 {{GLOSSARY}} 由 GlossaryManager 在构建时替换为当前术语表内容。
    /// </summary>
    private const string Glossary = @"我是游戏文本翻译家，原文翻译为{{TARGET_LAN}}，规则:
如无法翻译或缩略词难以理解则输出原文
新术语选词类型: 角色名，地名，组织
glossary只输出新术语，如无新术语则留空
json输出示例:
输入: {""1"":""「お前はなぜ監視官になった？」"",""2"":""「『レクシスを守るため』だと言っていたな。」""}
输出: {""1"":""「你为何成为了监视官？」"",""2"":""「你曾说过，是『为了保护雷克西斯』吧。」"",""glossary"":{""監視官"":""监视官"",""レクシス"":""雷克西斯""}}
当前术语表: {{GLOSSARY}}";

    /// <summary>
    /// 自定义提示词文件中的【普通模式】分节标题（INI 风格，独占一行）。
    /// 该标题行之下、下一个分节标题之前的内容即普通模式提示词
    /// （AutoGlossary=false 时使用）。
    /// </summary>
    public const string DefaultSectionHeader = "[普通模式提示词]";

    /// <summary>
    /// 自定义提示词文件中的【术语表模式】分节标题（INI 风格，独占一行）。
    /// 该标题行之下、文件结尾之前的内容即术语表模式提示词
    /// （AutoGlossary=true 时使用，应含 {{GLOSSARY}} 占位符）。
    /// </summary>
    public const string GlossarySectionHeader = "[自动术语表模式提示词]";

    /// <summary>自定义提示词单一文件名。</summary>
    private const string CustomPromptFileName = "AutoLLM_CustomPrompt.txt";

    /// <summary>术语表数据文件名（JSON 格式存储原文⇒译文映射）。</summary>
    private const string GlossaryFileName = "AutoLLM_Glossary.json";

    /// <summary>
    /// 返回已替换 {{SOURCE_LAN}} 和 {{TARGET_LAN}} 的【普通模式】系统提示词（不含 {{GLOSSARY}} 占位符）。
    /// 行尾归一化为 LF。config.CachedSystemPrompt 应存储此返回值。
    /// CustomPrompt=false 时直接使用内建 Default。
    /// </summary>
    public static string Build(AutoLLMConfig config)
    {
        string basePrompt = LoadPromptSection(config, wantGlossary: false, defaultPrompt: Default);

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
    /// CustomPrompt=false 时直接使用内建 Glossary。
    /// </summary>
    public static string BuildGlossaryPrompt(AutoLLMConfig config)
    {
        string basePrompt = LoadPromptSection(config, wantGlossary: true, defaultPrompt: Glossary);

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
    /// 文件采用 INI 风格分节，标题独占一行：
    ///   [普通模式提示词]
    ///   <普通模式提示词>
    ///
    ///   [自动术语表模式提示词]
    ///   <术语表模式提示词>
    /// 行为：
    ///   - CustomPrompt=false：直接用 defaultPrompt（不读文件）
    ///   - CustomPrompt=true：读 AutoLLM_CustomPrompt.txt
    ///       * 文件不存在：以 DefaultSectionHeader / GlossarySectionHeader 分节写入模板后，
    ///         按 wantGlossary 返回对应默认值
    ///       * 文件存在且含分节标题 → 按 wantGlossary 返回对应分节（分节为空回退到对应 Default/Glossary）
    ///       * 文件存在但无分节标题（最旧版整文件即提示词）→ 视整文件为普通模式提示词，
    ///         重写为 INI 分节格式（普通节=旧内容，术语表节=内建 Glossary），按 wantGlossary 返回对应内容
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
                var template = DefaultSectionHeader + "\n" + Default + "\n\n"
                    + GlossarySectionHeader + "\n" + Glossary;
                File.WriteAllText(path, template, Encoding.UTF8);
                Logger.Info("已创建自定义系统提示词模板（INI 风格，普通/术语表两节）: " + path);
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

            // 新格式：按 INI 风格分节标题切分
            string? defaultPart = ExtractSectionContent(content, DefaultSectionHeader, GlossarySectionHeader);
            string? glossaryPart = ExtractSectionContent(content, GlossarySectionHeader, null);

            if (defaultPart == null && glossaryPart == null)
            {
                // 最旧版格式：整个文件即为普通模式提示词（无分节标题）。
                // 视旧内容为普通模式提示词，术语表节用内建 Glossary，
                // 重写为 INI 分节格式后按 wantGlossary 返回对应内容。
                var oldPrompt = content.Trim('\r', '\n', ' ', '\t');
                if (oldPrompt.Length == 0)
                {
                    Logger.Warn("自定义提示词文件为空，回退到内建默认提示词");
                    return wantGlossary ? Glossary : Default;
                }

                Logger.Warn("检测到旧版自定义提示词文件（无分节标题），正在重写为新版 INI 分节格式: " + path);
                try
                {
                    var template = DefaultSectionHeader + "\n" + oldPrompt + "\n\n"
                        + GlossarySectionHeader + "\n" + Glossary;
                    File.WriteAllText(path, template, Encoding.UTF8);
                    Logger.Info("已将旧版自定义提示词重写为 INI 分节格式: " + path);
                }
                catch (Exception ex)
                {
                    Logger.Error("重写旧版自定义提示词文件失败（本次仍按旧内容返回）", ex);
                }

                defaultPart = oldPrompt;
                glossaryPart = Glossary;
            }

            if (wantGlossary)
            {
                if (glossaryPart == null || glossaryPart.Trim().Length == 0)
                {
                    Logger.Warn("自定义提示词文件术语表分节为空，回退到内建术语表默认提示词");
                    return Glossary;
                }
                return glossaryPart;
            }
            else
            {
                if (defaultPart == null || defaultPart.Trim().Length == 0)
                {
                    Logger.Warn("自定义提示词文件普通模式分节为空，回退到内建默认提示词");
                    return Default;
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

    /// <summary>
    /// 提取 INI 风格分节标题之间的内容。
    /// 返回 <paramref name="sectionHeader"/> 标题行之后、<paramref name="nextHeader"/> 标题行之前
    /// （或文件末尾，当 <paramref name="nextHeader"/> 为 null 时）的内容。
    /// 分节标题须独占一行（行内仅可有可选空白与可选前导注释符 '#'）；找不到返回 null。
    /// 返回内容已去除首尾空白/换行。
    /// </summary>
    private static string? ExtractSectionContent(string content, string sectionHeader, string? nextHeader)
    {
        // 按行拆分（保留原样，仅统一行尾以便行内匹配）
        var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        int contentStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsSectionHeaderLine(lines[i], sectionHeader))
            {
                contentStart = i + 1;
                break;
            }
        }
        if (contentStart < 0) return null;

        int contentEnd = lines.Length;
        if (nextHeader != null)
        {
            for (int i = contentStart; i < lines.Length; i++)
            {
                if (IsSectionHeaderLine(lines[i], nextHeader))
                {
                    contentEnd = i;
                    break;
                }
            }
        }

        // 拼接 [contentStart, contentEnd) 行，去除首尾空行
        var sb = new StringBuilder();
        for (int i = contentStart; i < contentEnd; i++)
        {
            sb.Append(lines[i]);
            if (i < contentEnd - 1) sb.Append('\n');
        }
        return sb.ToString().Trim('\n', ' ', '\t');
    }

    /// <summary>
    /// 判断一行是否为分节标题行。
    /// 允许前后空白，且允许可选前导注释符 '#' 使标题行形如 "  # [普通模式提示词]"。
    /// 标题文本必须独占该行（标题之后不得有其它非空白字符）。
    /// </summary>
    private static bool IsSectionHeaderLine(string line, string header)
    {
        var trimmed = line.Trim(' ', '\t');
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(1).TrimStart(' ', '\t');
        }
        return trimmed == header;
    }

    /// <summary>将 CRLF 和孤立 CR 统一为 LF。先处理 CRLF 再处理 CR，避免重复换行。</summary>
    private static string NormalizeLineEndings(string s)
    {
        return s.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}