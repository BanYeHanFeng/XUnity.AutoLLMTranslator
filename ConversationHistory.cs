using System;
using System.Collections.Generic;

internal class ConversationHistory
{
    List<object> _history = new List<object>();
    readonly object _lock = new object();
    int _clearCount = 0;

    public bool Enabled { get; set; }
    public int MaxContext { get; set; }
    public int TurnCount { get { lock (_lock) return _history.Count / 2; } }

    public List<object> BuildMessages(string systemPrompt, string inputJson)
    {
        var messages = new List<object>();
        messages.Add(new Dictionary<string, object> { {"role", "system"}, {"content", systemPrompt} });
        lock (_lock)
        {
            foreach (var msg in _history)
                messages.Add(msg);
        }
        messages.Add(new Dictionary<string, object> { {"role", "user"}, {"content", inputJson} });
        return messages;
    }

    public void AppendExchange(string inputJson, string responseJson)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            _history.Add(new Dictionary<string, object> { {"role", "user"}, {"content", inputJson} });
            _history.Add(new Dictionary<string, object> { {"role", "assistant"}, {"content", responseJson} });
        }
    }

    public void CheckAndClearIfOverLimit(string systemPrompt, string inputJson)
    {
        if (MaxContext <= 0 || !Enabled) return;
        int chars = systemPrompt.Length + inputJson.Length;
        lock (_lock)
        {
            foreach (var msg in _history)
            {
                if (msg is Dictionary<string, object> dict && dict.ContainsKey("content"))
                    chars += (dict["content"] as string)?.Length ?? 0;
            }
            int estimatedTokens = chars / 2;
            Logger.Debug($"上下文估算: {estimatedTokens}/{MaxContext} tokens (字符{chars}, 历史{_history.Count / 2}轮)");
            if (estimatedTokens > MaxContext)
            {
                _history.Clear();
                _clearCount++;
                Logger.Info($"历史超出 MaxContext({MaxContext})，已清空对话历史（第{_clearCount}次）");
            }
        }
    }

}
