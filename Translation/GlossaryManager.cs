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
    /// 渲染术语表为提示词文本（供系统提示词 {{GLOSSARY}} 占位符替换）。
    /// 格式：每行 "原文 => 译文"，无术语时返回 "（无）"。
    /// </summary>
    public string RenderForPrompt()
    {
        lock (_lock)
        {
            if (_glossary.Count == 0) return "（无）";
            var sb = new StringBuilder();
            foreach (var kvp in _glossary)
            {
                sb.Append(kvp.Key).Append(" => ").Append(kvp.Value).Append('\n');
            }
            // 去掉末尾换行
            if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
                sb.Length--;
            return sb.ToString();
        }
    }

    /// <summary>
    /// 记录本轮从模型响应中提取的新术语（暂存内存，不立即写文件）。
    /// 重复术语以新值为准（模型可能在后续翻译中修正译名）。
    /// </summary>
    public void AddPendingTerms(Dictionary<string, object>? glossaryFromModel)
    {
        if (!Enabled || glossaryFromModel == null || glossaryFromModel.Count == 0) return;
        lock (_lock)
        {
            foreach (var kvp in glossaryFromModel)
            {
                string? translated = kvp.Value as string;
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(translated)) continue;
                _pendingNew[kvp.Key] = translated!;
            }
        }
    }

    /// <summary>
    /// 将 pending 新术语合并到文件并重载。
    /// 在对话历史清空时调用：历史清空意味着上下文重置，此时把本轮收集的术语落盘。
    /// 返回新增条目数（含更新）。
    /// </summary>
    public int MergePendingAndSave()
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
            {
                SaveToFile();
                Logger.Info("术语表已更新: +" + added + " 条, 共" + _glossary.Count + "条");
            }
            return added;
        }
    }

    /// <summary>
    /// 构建完整的系统提示词：将术语表内容填入模板的 {{GLOSSARY}} 占位符。
    /// 模板来自 config.CachedGlossaryPrompt（已替换语言占位符，仅保留 {{GLOSSARY}}）。
    /// </summary>
    public string BuildSystemPrompt(string glossaryPromptTemplate)
    {
        var glossaryText = RenderForPrompt();
        return glossaryPromptTemplate.Replace("{{GLOSSARY}}", glossaryText);
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
            // 序列化为 {"原文":"译文"} 格式
            var dict = new Dictionary<string, object>();
            foreach (var kvp in _glossary)
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
