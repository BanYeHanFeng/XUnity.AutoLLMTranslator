using System;
using System.Collections.Generic;


internal class ConversationHistory
{
    private readonly List<LlmMessage> _history = new List<LlmMessage>();
    private readonly object _lock = new object();
    private int _cachedHistoryChars = 0;
    private int _clearCount = 0;

    public bool Enabled { get; set; }
    public int MaxContext { get; set; }
    public int TurnCount { get { lock (_lock) return _history.Count / 2; } }

    /// <summary>构建完整消息列表：[system, ...history, user]。</summary>
    public List<LlmMessage> BuildMessages(string systemPrompt, string userInput)
    {
        var messages = new List<LlmMessage>();
        messages.Add(new LlmMessage { Role = "system", Content = systemPrompt });
        lock (_lock)
        {
            foreach (var msg in _history)
                messages.Add(msg);
        }
        messages.Add(new LlmMessage { Role = "user", Content = userInput });
        return messages;
    }

    /// <summary>追加一轮对话（user + assistant）。</summary>
    public void AppendExchange(string userInput, string assistantOutput)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            _history.Add(new LlmMessage { Role = "user", Content = userInput });
            _history.Add(new LlmMessage { Role = "assistant", Content = assistantOutput });
            _cachedHistoryChars += userInput.Length + assistantOutput.Length;
        }
    }

    /// <summary>检查上下文是否超限，超限则清空历史。</summary>
    public void CheckAndClearIfOverLimit(string systemPrompt, string userInput)
    {
        if (MaxContext <= 0 || !Enabled) return;
        lock (_lock)
        {
            int chars = systemPrompt.Length + userInput.Length + _cachedHistoryChars;
            int estimatedTokens = chars / 2;   // 粗估：2字符≈1token
            if (Logger.IsDebugEnabled)
                Logger.Debug("上下文估算: " + estimatedTokens + "/" + MaxContext + " tokens " +
                    "(字符" + chars + ", 历史" + (_history.Count / 2) + "轮)");
            if (estimatedTokens > MaxContext)
            {
                _history.Clear();
                _cachedHistoryChars = 0;
                _clearCount++;
                Logger.Info("历史超出 MaxContext(" + MaxContext + ")，已清空对话历史（第" + _clearCount + "次）");
            }
        }
    }
}
