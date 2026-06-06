using System;
using System.Collections.Generic;


internal class ConversationHistory
{
    private readonly List<LlmMessage> _history = new List<LlmMessage>();
    private readonly object _lock = new object();
    private int _systemPromptTokens = 0;
    private int _totalContextTokens = 0;
    private bool _apiReturnsTokens = false;
    private int _discardCount = 0;
    private int _clearCount = 0;

    public bool Enabled { get; set; }
    public int MaxContext { get; set; }

    /// <summary>当前上下文 token 总数（含 system prompt + 历史对话）。精确模式优先，回退估算。</summary>
    public int TotalContextTokens { get { lock (_lock) return _totalContextTokens; } }

    /// <summary>API 是否支持返回 token 统计。</summary>
    public bool ApiReturnsTokens { get { lock (_lock) return _apiReturnsTokens; } }

    /// <summary>历史对话轮数。</summary>
    public int TurnCount { get { lock (_lock) return _history.Count / 2; } }

    /// <summary>单条超限被丢弃的任务数。</summary>
    public int DiscardCount { get { lock (_lock) return _discardCount; } }

    /// <summary>历史清空次数。</summary>
    public int ClearCount { get { lock (_lock) return _clearCount; } }

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

    /// <summary>初始化系统提示词 token 估算（构造函数中调用，MaxContext 设置之后）。</summary>
    public void InitSystemPrompt(string prompt)
    {
        lock (_lock)
        {
            _systemPromptTokens = prompt.Length * 3 / 4;
            _totalContextTokens = _systemPromptTokens;
        }
        if (Logger.IsDebugEnabled)
            Logger.Debug("Token估算: systemPrompt=" + _systemPromptTokens + " tokens (chars=" + prompt.Length + "*0.75)");
    }

    /// <summary>估算纯文本的 token 数（0.75 字符/token）。</summary>
    public int EstimateTokens(string text)
    {
        return text.Length * 3 / 4;
    }

    /// <summary>检查上下文是否超限，超限则清空历史。</summary>
    public void CheckAndClearIfOverLimit()
    {
        if (MaxContext <= 0 || !Enabled) return;
        lock (_lock)
        {
            if (_totalContextTokens > MaxContext)
            {
                int oldTokens = _totalContextTokens;
                _history.Clear();
                _totalContextTokens = _systemPromptTokens;
                _clearCount++;
                Logger.Info("历史清空: token=" + oldTokens + " > MaxContext(" + MaxContext + "), 清空次数=" + _clearCount);
            }
            if (Logger.IsDebugEnabled)
                Logger.Debug("上下文状态: " + _totalContextTokens + "/" + MaxContext + " tokens, 历史" + (_history.Count / 2) + "轮, 清空" + _clearCount + "次");
        }
    }

    /// <summary>记录 API 返回的精确 token 统计。</summary>
    public void RecordApiUsage(long promptTokens, long completionTokens)
    {
        if (promptTokens == 0 && completionTokens == 0) return;
        lock (_lock)
        {
            if (!_apiReturnsTokens)
            {
                _apiReturnsTokens = true;
                Logger.Info("Token追踪: API 返回精确 token 统计，切换为精确模式");
            }
            int newTotal = (int)(promptTokens + completionTokens);
            if (Logger.IsDebugEnabled)
                Logger.Debug("Token精确更新: prompt=" + promptTokens + " completion=" + completionTokens
                    + " total=" + newTotal + " (旧值=" + _totalContextTokens + ")");
            _totalContextTokens = newTotal;
        }
    }

    /// <summary>记录一轮对话交换（user + assistant）。
    /// 精确模式下只追加消息，回退模式下累加估算 token。</summary>
    public void RecordExchange(string userInput, string assistantOutput)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            _history.Add(new LlmMessage { Role = "user", Content = userInput });
            _history.Add(new LlmMessage { Role = "assistant", Content = assistantOutput });
            if (!_apiReturnsTokens)
            {
                int added = (userInput.Length + assistantOutput.Length) * 3 / 4;
                _totalContextTokens += added;
                if (Logger.IsDebugEnabled)
                    Logger.Debug("Token估算累加: +" + added + " tokens (user=" + userInput.Length
                        + "chars, assistant=" + assistantOutput.Length + "chars), 累积=" + _totalContextTokens);
            }
        }
    }

    /// <summary>单条超限丢弃计数 +1。</summary>
    public void IncrementDiscardCount()
    {
        lock (_lock)
        {
            _discardCount++;
        }
    }

    /// <summary>强制清空历史（上下文接近上限但未触发自动清空时调用）。</summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            int oldTokens = _totalContextTokens;
            _history.Clear();
            _totalContextTokens = _systemPromptTokens;
            _clearCount++;
            Logger.Info("历史清空: token=" + oldTokens + " (已接近MaxContext " + MaxContext + "), 清空次数=" + _clearCount);
        }
    }
}
