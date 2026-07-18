using System;
using System.Collections.Generic;
using System.IO;
using System.Text;


internal class GlossaryManager
{
    private readonly string? _filePath;
    private readonly object _lock = new object();
    private readonly Dictionary<string, string> _glossary = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _pendingNew = new Dictionary<string, string>();

    /// <summary>术语表是否启用（文件路径非空即启用）。</summary>
    public bool Enabled => !string.IsNullOrEmpty(_filePath);

    /// <summary>当前术语表条目数（文件已加载部分，不含 pending）。</summary>
    public int Count { get { lock (_lock) return _glossary.Count; } }

    /// <summary>待合并的新术语条目数。</summary>
    public int PendingCount { get { lock (_lock) return _pendingNew.Count; } }

    public GlossaryManager(string? filePath)
    {
        _filePath = filePath;
        if (!Enabled) return;
        LoadFromFile();
    }

    /// <summary>
    /// 渲染术语表为提示词文本（供系统提示词 {{术语表}} 占位符替换）。
    /// 格式：单行，条目间以逗号分隔，每条 "原文:译文"，无术语时返回 "（无）"。
    /// </summary>
    public string RenderForPrompt()
    {
        lock (_lock)
        {
            if (_glossary.Count == 0) return "（无）";
            var sb = new StringBuilder();
            bool first = true;
            foreach (var kvp in _glossary)
            {
                if (!first) sb.Append(',');
                sb.Append(kvp.Key).Append(':').Append(kvp.Value);
                first = false;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 记录本轮从模型响应中提取的新术语到内存缓冲 _pendingNew，并立即全量落盘。
    /// 每批有新术语即写文件（_glossary + _pendingNew 合并视图），防止游戏意外停止导致丢失。
    /// 新术语仅暂存 _pendingNew，<b>不</b>合并进 _glossary；只有对话历史清空时才由 MergePending
    /// 注入 _glossary（进而更新系统提示词），以保证一轮对话上下文内术语表保持一致。
    /// 重复术语以新值为准（模型可能在后续翻译中修正译名）。
    /// </summary>
    public void AddPendingTerms(Dictionary<string, object>? glossaryFromModel)
    {
        if (!Enabled || glossaryFromModel == null || glossaryFromModel.Count == 0) return;
        lock (_lock)
        {
            bool anyNew = false;
            foreach (var kvp in glossaryFromModel)
            {
                string? translated = kvp.Value as string;
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(translated)) continue;
                // 仅当键新增或值变化时算作新条目（避免无变化时反复写盘）
                if (!_pendingNew.TryGetValue(kvp.Key, out var existing) || existing != translated)
                {
                    _pendingNew[kvp.Key] = translated!;
                    anyNew = true;
                }
            }
            if (anyNew)
            {
                // 立即落盘 _glossary + _pendingNew 全量（pending 覆盖同名旧值）
                SaveToFile();
                Logger.Info("术语表已即时落盘(防丢失): 暂存" + _pendingNew.Count +
                    "条(待历史清空后注入), 文件共" + (_glossary.Count + _pendingNew.Count) + "条");
            }
        }
    }

    /// <summary>
    /// 将缓冲中的新术语注入 _glossary（使其进入系统提示词）。
    /// 在对话历史清空时调用：历史清空意味着上下文重置，此时把暂存术语并入 _glossary，
    /// 让后续新对话使用更新后的术语表。
    /// 文件已由 AddPendingTerms 每批即时落盘，此处仅做内存合并，无需再写文件。
    /// 返回新增条目数（含更新）。
    /// </summary>
    public int MergePending()
    {
        if (!Enabled) return 0;
        lock (_lock)
        {
            if (_pendingNew.Count == 0) return 0;

            int added = 0;
            foreach (var kvp in _pendingNew)
            {
                if (!_glossary.TryGetValue(kvp.Key, out var existing) || existing != kvp.Value)
                {
                    _glossary[kvp.Key] = kvp.Value;
                    added++;
                }
            }
            _pendingNew.Clear();

            if (added > 0)
                Logger.Info("术语表已注入: +" + added + " 条, 共" + _glossary.Count + "条");
            return added;
        }
    }

    /// <summary>
    /// 构建完整的系统提示词：将术语表内容填入模板的 {{术语表}} 占位符。
    /// 模板来自 config.CachedTranslationPrompt（翻译线程）或 config.CachedExtractionPrompt（术语抽取线程），
/// 已替换语言占位符，仅保留 {{术语表}} 由本方法在运行时填充。
    /// </summary>
    public string BuildSystemPrompt(string glossaryPromptTemplate)
    {
        var glossaryText = RenderForPrompt();
        return glossaryPromptTemplate.Replace("{{术语表}}", glossaryText);
    }

    // ---- 内部方法 ----

    private void LoadFromFile()
    {
        try
        {
            if (!File.Exists(_filePath!))
            {
                // 首次运行：创建空 JSON 文件
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath!)!);
                File.WriteAllText(_filePath!, "{}", Encoding.UTF8);
                Logger.Info("已创建空术语表文件: " + _filePath);
                return;
            }

            var json = File.ReadAllText(_filePath!, Encoding.UTF8);
            var parsed = SimpleJson.ParseJsonObject(json);
            lock (_lock)
            {
                _glossary.Clear();
                foreach (var kvp in parsed)
                {
                    string? val = kvp.Value as string;
                    if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(val))
                        _glossary[kvp.Key] = val!;
                }
            }
            Logger.Info("已加载术语表: " + _glossary.Count + " 条");
        }
        catch (Exception ex)
        {
            Logger.Error("加载术语表文件失败", ex);
        }
    }

    private void SaveToFile()
    {
        try
        {
            // 序列化为 {"原文":"译文"} 格式。
            // 写入 _glossary + _pendingNew 的合并视图（pending 覆盖同名旧值），
            // 这样每批新增的暂存术语即便尚未注入 _glossary 也能落盘，防止游戏意外停止丢失。
            var dict = new Dictionary<string, object>();
            foreach (var kvp in _glossary)
                dict[kvp.Key] = kvp.Value;
            foreach (var kvp in _pendingNew)
                dict[kvp.Key] = kvp.Value;
            var json = SimpleJson.Serialize(dict);
            File.WriteAllText(_filePath!, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Error("保存术语表文件失败", ex);
        }
    }
}
