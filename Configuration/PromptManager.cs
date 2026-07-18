using System;
using System.IO;
using System.Text;


internal static class PromptManager
{
    // ---- 占位符（统一中文） ----
    // 所有系统提示词占位符统一为中文，便于用户在自定义提示词文件中直接阅读/修改：
    //   {{源语言}} {{目标语言}} {{术语表}} {{最近原文}}
    // 旧英文占位符（{{SOURCE_LAN}}/{{TARGET_LAN}}/{{GLOSSARY}}/{{RECENT}}）在加载自定义
    // 提示词文件时由 MigratePlaceholders 自动迁移为对应的中文占位符并 Warn 一次。

    /// <summary>
    /// 默认【普通模式】系统提示词模板（AutoGlossary=false 时使用）。
    /// 输出结构与翻译解析器一致：{"1":"译文1","2":"译文2"}。
    /// </summary>
    private const string Default = @"我是游戏文本翻译家，原文翻译为{{目标语言}}，规则:
如无法翻译或缩略词难以理解则输出原文
json输出示例:
输入: {""1"":""「お前はなぜ監視官になった？」"",""2"":""「『レクシスを守るため』だと言っていたな。」""}
输出: {""1"":""「你为何成为了监视官？」"",""2"":""「你曾说过，是『为了保护雷克西斯』吧。」""}";

    /// <summary>
    /// 默认【翻译模式】系统提示词模板（AutoGlossary=true 时由翻译线程使用）。
    /// 内嵌当前术语表（{{术语表}}），仅要求模型按术语表统一译名、产出译文，
    /// 不要求模型在响应中输出 glossary（术语抽取由独立的术语线程负责）。
    /// 输出结构：{"1":"译文1","2":"译文2"}。
    /// </summary>
    private const string TranslationWithGlossary = @"我是游戏文本翻译家，原文翻译为{{目标语言}}，规则:
如无法翻译或缩略词难以理解则输出原文
专有名词必须严格遵循下方术语表的既有译名，保持一致
json输出示例:
输入: {""1"":""「お前はなぜ監視官になった？」"",""2"":""「『レクシスを守るため』だと言っていたな。」""}
输出: {""1"":""「你为何成为了监视官？」"",""2"":""「你曾说过，是『为了保护雷克西斯』吧。」""}
当前术语表: {{术语表}}";

    /// <summary>
    /// 默认【术语抽取模式】系统提示词模板（AutoGlossary=true 时由术语抽取线程使用）。
    /// 只要求模型从用户消息所附最近原文中识别需要统一译法的新专名，输出 {"glossary":{"原文":"译文"}}，
    /// 不产出译文。{{术语表}} 用于让模型跳过已有条目；最近原文由 GlossaryWorker 作为 user 消息
    /// 附带（非 LLM 对话历史，无前缀缓存/同步耦合），系统提示词本体保持稳定以利于前缀缓存。
    /// </summary>
    private const string GlossaryExtractionOnly = @"你是游戏术语抽取助手。从用户消息所附最近原文中识别需要统一译法的新术语（角色名、地名、组织名等专有名词）。
规则:
- 仅输出本批新出现的专名，已在当前术语表中的不要重复输出
- 无新术语时输出空对象
- 译文使用{{目标语言}}
当前术语表: {{术语表}}
json输出格式: {""glossary"":{""原文"":""译文""}}";

    // ---- 自定义提示词文件分节标题（INI 风格，独占一行） ----

    /// <summary>【普通模式】分节标题（AutoGlossary=false 时使用）。</summary>
    public const string DefaultSectionHeader = "[普通模式提示词]";

    /// <summary>【翻译模式】分节标题（AutoGlossary=true 时由翻译线程使用，应含 {{术语表}}）。</summary>
    public const string TranslationSectionHeader = "[翻译模式提示词]";

    /// <summary>【术语抽取模式】分节标题（AutoGlossary=true 时由术语线程使用，应含 {{术语表}} 与 {{最近原文}}）。</summary>
    public const string ExtractionSectionHeader = "[术语抽取模式提示词]";

    /// <summary>自定义提示词单一文件名。</summary>
    private const string CustomPromptFileName = "AutoLLM_CustomPrompt.txt";

    /// <summary>术语表数据文件名（JSON 格式存储原文⇒译文映射）。</summary>
    private const string GlossaryFileName = "AutoLLM_Glossary.json";

    /// <summary>
    /// 返回已替换 {{源语言}}/{{目标语言}} 的【普通模式】系统提示词（不含 {{术语表}} 占位符）。
    /// 行尾归一化为 LF。config.CachedSystemPrompt 应存储此返回值。
    /// CustomPrompt=false 时直接使用内建 Default。
    /// </summary>
    public static string Build(AutoLLMConfig config)
    {
        string basePrompt = LoadPromptSection(config, PromptKind.Default, Default);

        basePrompt = NormalizeLineEndings(basePrompt);
        Logger.Info("系统提示词: " + basePrompt.Length + " 字符");

        return basePrompt
            .Replace("{{源语言}}", config.SourceLanguage ?? "")
            .Replace("{{目标语言}}", config.DestinationLanguage ?? "");
    }

    /// <summary>
    /// 返回已替换 {{源语言}}/{{目标语言}} 的【翻译模式】系统提示词模板。
    /// 保留 {{术语表}} 占位符，由 GlossaryManager.BuildSystemPrompt 在运行时填充。
    /// 仅在 AutoGlossary=true 时调用；同时设置 config.GlossaryPath。
    /// CustomPrompt=false 时直接使用内建 TranslationWithGlossary。
    /// </summary>
    public static string BuildTranslationPrompt(AutoLLMConfig config)
    {
        EnsureGlossaryPath(config);

        string basePrompt = LoadPromptSection(config, PromptKind.Translation, TranslationWithGlossary);

        basePrompt = NormalizeLineEndings(basePrompt);
        Logger.Info("翻译模式提示词: " + basePrompt.Length + " 字符");

        return basePrompt
            .Replace("{{源语言}}", config.SourceLanguage ?? "")
            .Replace("{{目标语言}}", config.DestinationLanguage ?? "");
    }

    /// <summary>
    /// 返回已替换 {{目标语言}} 的【术语抽取模式】系统提示词模板。
    /// 保留 {{术语表}} 与 {{最近原文}} 占位符，由 GlossaryWorker 在运行时填充。
    /// CustomPrompt=false 时直接使用内建 GlossaryExtractionOnly。
    /// </summary>
    public static string BuildGlossaryExtractionPrompt(AutoLLMConfig config)
    {
        EnsureGlossaryPath(config);

        string basePrompt = LoadPromptSection(config, PromptKind.Extraction, GlossaryExtractionOnly);

        basePrompt = NormalizeLineEndings(basePrompt);
        Logger.Info("术语抽取提示词: " + basePrompt.Length + " 字符");

        return basePrompt
            .Replace("{{源语言}}", config.SourceLanguage ?? "")
            .Replace("{{目标语言}}", config.DestinationLanguage ?? "");
    }

    /// <summary>设置术语表文件路径（供 GlossaryManager 使用）。BepInExRoot 缺失则跳过。</summary>
    private static void EnsureGlossaryPath(AutoLLMConfig config)
    {
        if (string.IsNullOrEmpty(config.BepInExRoot) || !string.IsNullOrEmpty(config.GlossaryPath))
            return;
        config.GlossaryPath = Path.Combine(
            Path.Combine(config.BepInExRoot!, "config"), GlossaryFileName);
    }

    /// <summary>提示词种类，决定从自定义文件中加载哪个分节。</summary>
    private enum PromptKind { Default, Translation, Extraction }

    /// <summary>
    /// 从单一自定义提示词文件加载并按 kind 选取对应分节。文件采用 INI 风格分节，标题独占一行：
    ///   [普通模式提示词]            普通模式
    ///   [翻译模式提示词]            翻译模式（AutoGlossary=true，翻译线程）
    ///   [术语抽取模式提示词]        术语抽取模式（AutoGlossary=true，术语线程）
    /// 行为：
    ///   - CustomPrompt=false：直接用 defaultPrompt（不读文件）
    ///   - CustomPrompt=true：读 AutoLLM_CustomPrompt.txt
    ///       * 文件不存在：写入三节模板后返回 defaultPrompt
    ///       * 文件存在且含对应分节标题 → 返回对应分节（分节为空回退到 defaultPrompt）
    ///       * 文件存在但无任何分节标题（最旧版整文件即提示词）→ 视整文件为普通模式提示词，
    ///         重写为三节 INI 格式后按 kind 返回对应内容
    ///   - BepInExRoot 缺失：回退 defaultPrompt
    /// 加载前会先把旧英文占位符迁移为中文占位符并按需回写文件（Warn 一次）。
    /// </summary>
    private static string LoadPromptSection(AutoLLMConfig config, PromptKind kind, string defaultPrompt)
    {
        if (!config.CustomPrompt)
        {
            Logger.Info("使用默认系统提示词(" + KindName(kind) + ")");
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
            // 首次运行：生成含三节模板的文件，方便用户修改
            try
            {
                Directory.CreateDirectory(Path.Combine(config.BepInExRoot!, "config"));
                var template = DefaultSectionHeader + "\n" + Default + "\n\n"
                    + TranslationSectionHeader + "\n" + TranslationWithGlossary + "\n\n"
                    + ExtractionSectionHeader + "\n" + GlossaryExtractionOnly;
                File.WriteAllText(path, template, Encoding.UTF8);
                Logger.Info("已创建自定义系统提示词模板（INI 风格，普通/翻译/术语抽取三节）: " + path);
            }
            catch (Exception ex)
            {
                Logger.Error("创建自定义系统提示词模板失败", ex);
            }
            // 不论写入是否成功，首次都返回默认提示词
            return defaultPrompt;
        }

        // 文件已存在
        try
        {
            string content = File.ReadAllText(path, Encoding.UTF8);
            content = MigratePlaceholdersIfNeeded(path, content);
            Logger.Info("已加载自定义系统提示词: " + path);

            string? defaultPart = ExtractSectionContent(content, DefaultSectionHeader, TranslationSectionHeader);
            string? translationPart = ExtractSectionContent(content, TranslationSectionHeader, ExtractionSectionHeader);
            string? extractionPart = ExtractSectionContent(content, ExtractionSectionHeader, null);

            if (defaultPart == null && translationPart == null && extractionPart == null)
            {
                // 最旧版格式：整个文件即为普通模式提示词（无分节标题）。
                // 视旧内容为普通模式提示词，翻译/术语抽取节用内建模板，
                // 重写为三节 INI 格式后按 kind 返回对应内容。
                var oldPrompt = content.Trim('\r', '\n', ' ', '\t');
                if (oldPrompt.Length == 0)
                {
                    Logger.Warn("自定义提示词文件为空，回退到内建默认提示词");
                    return defaultPrompt;
                }

                Logger.Warn("检测到旧版自定义提示词文件（无分节标题），正在重写为三节 INI 分节格式: " + path);
                try
                {
                    var template = DefaultSectionHeader + "\n" + oldPrompt + "\n\n"
                        + TranslationSectionHeader + "\n" + TranslationWithGlossary + "\n\n"
                        + ExtractionSectionHeader + "\n" + GlossaryExtractionOnly;
                    File.WriteAllText(path, template, Encoding.UTF8);
                    Logger.Info("已将旧版自定义提示词重写为三节 INI 分节格式: " + path);
                }
                catch (Exception ex)
                {
                    Logger.Error("重写旧版自定义提示词文件失败（本次仍按旧内容返回）", ex);
                }

                defaultPart = oldPrompt;
                translationPart = TranslationWithGlossary;
                extractionPart = GlossaryExtractionOnly;
            }

            switch (kind)
            {
                case PromptKind.Default:
                    if (defaultPart == null || defaultPart.Trim().Length == 0)
                    {
                        Logger.Warn("自定义提示词文件普通模式分节为空，回退到内建默认提示词");
                        return Default;
                    }
                    return defaultPart;
                case PromptKind.Translation:
                    if (translationPart == null || translationPart.Trim().Length == 0)
                    {
                        Logger.Warn("自定义提示词文件翻译模式分节为空，回退到内建翻译模式默认提示词");
                        return TranslationWithGlossary;
                    }
                    return translationPart;
                case PromptKind.Extraction:
                    if (extractionPart == null || extractionPart.Trim().Length == 0)
                    {
                        Logger.Warn("自定义提示词文件术语抽取分节为空，回退到内建术语抽取默认提示词");
                        return GlossaryExtractionOnly;
                    }
                    return extractionPart;
                default:
                    return defaultPrompt;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取自定义系统提示词失败", ex);
            return defaultPrompt;
        }
    }

    /// <summary>
    /// 把旧英文占位符迁移为中文占位符；若发生替换则回写文件并 Warn 一次。
    /// 旧 → 新：{{TARGET_LAN}}→{{目标语言}} {{SOURCE_LAN}}→{{源语言}}
    ///         {{GLOSSARY}}→{{术语表}} {{RECENT}}→{{最近原文}}
    /// </summary>
    private static string MigratePlaceholdersIfNeeded(string path, string content)
    {
        string migrated = content
            .Replace("{{TARGET_LAN}}", "{{目标语言}}")
            .Replace("{{SOURCE_LAN}}", "{{源语言}}")
            .Replace("{{GLOSSARY}}", "{{术语表}}")
            .Replace("{{RECENT}}", "{{最近原文}}");
        if (migrated == content) return content;

        Logger.Warn("检测到旧英文占位符，已迁移为中文占位符并回写自定义提示词文件: " + path);
        try
        {
            File.WriteAllText(path, migrated, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Error("回写迁移后的自定义提示词文件失败（本次仍按迁移后内容返回）", ex);
        }
        return migrated;
    }

    private static string KindName(PromptKind kind)
    {
        switch (kind)
        {
            case PromptKind.Default: return "普通模式";
            case PromptKind.Translation: return "翻译模式";
            case PromptKind.Extraction: return "术语抽取模式";
            default: return kind.ToString();
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