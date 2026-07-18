using System;
using System.Collections.Generic;
using System.Globalization;


/// <summary>
/// 对话历史与系统提示词基线容器。
///
/// 双线程架构（翻译线程 + 术语抽取线程）下 <see cref="Enabled"/> 设为 false：
///   - <see cref="RecordExchange"/>/RecordApiUsage/CheckAndClearIfOverLimit/ClearHistory 自动短路，
///     两条线程的 LLM 调用彼此无共享可变历史，消除同步问题；前缀缓存命中随轮数不再衰减。
///   - 本类现仅保留：系统提示词 token 基线（<see cref="InitSystemPrompt"/>/TotalContextTokens，
///     供 <c>SelectBatch</c> 单批容量估算）、<see cref="AllocKeys"/>（编号键单调递增避免跨批重号）、
///     <see cref="EstimateTokens"/>、丢弃/清空计数等只读/局部状态。
///   - 术语表合并不再借历史清空事件驱动，改由 <c>GlossaryWorker</c> 按阈值单点触发。
/// </summary>
internal class ConversationHistory
{
    private readonly List<LlmMessage> _history = new List<LlmMessage>();
    private readonly object _lock = new object();
    private int _systemPromptTokens = 0;
    private int _totalContextTokens = 0;
    private bool _apiReturnsTokens = false;
    private int _discardCount = 0;
    private int _clearCount = 0;

    // 输入/输出 JSON 的编号键："1"/"2"/... 从 1 开始单调递增；同一对话历史窗口内每个
    // 编号唯一，杜绝旧实现跨批次重号（模型把不同批次同号条目拼成同一对象）的混淆。
    // 历史清空时（CheckAndClearIfOverLimit / ClearHistory / UpdateSystemPrompt 三处入口）
    // 一并重置回 1 —— 此时历史已无旧批条目，模型不会再见到这些编号，故从 1 重新递增安全。
    private int _nextKey = 1;

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
    }

    /// <summary>
    /// 为批内任务分配本批 JSON 输入/输出键 "1"/"2"/...（_nextKey 当前值即起始键）。
    /// ParallelCount 已废弃固定为 1，无并发批次，键的递增在锁内一次完成即可。
    /// 每个 task.UserKey 写入其对应的编号字符串；Advance() 仅发生在分配处。
    /// 调用方负责在 RecordExchange（成功）后才把对应轮次记录入库——键的递增与历史
    /// 记录是两个独立维度：键递增不可回退（避免与历史旧轮重号），即便批次最终失败
    /// 重试也只是排到下一批再分配新一组键（与首次键不同，但不重号）。
    /// </summary>
    public void AllocKeys(List<TranslationTask> batch)
    {
        lock (_lock)
        {
            int k = _nextKey;
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].UserKey = k.ToString(CultureInfo.InvariantCulture);
                k++;
            }
            _nextKey = k;
        }
    }

    /// <summary>
    /// 更新系统提示词及其 token 估算（术语表合并后系统提示词变长时调用）。
    /// 会重置历史并基于新系统提示词重新计算基线 token。
    /// </summary>
    public void UpdateSystemPrompt(string prompt)
    {
        lock (_lock)
        {
            _systemPromptTokens = prompt.Length * 3 / 4;
            _history.Clear();
            _totalContextTokens = _systemPromptTokens;
            _nextKey = 1;   // 历史清空 → 编号键重置回 1
        }
    }

    /// <summary>估算纯文本的 token 数（0.75 字符/token）。</summary>
    public int EstimateTokens(string text)
    {
        return text.Length * 3 / 4;
    }

    /// <summary>检查上下文是否超限，超限则清空历史。返回 true 表示触发了清空。</summary>
    public bool CheckAndClearIfOverLimit()
    {
        if (MaxContext <= 0 || !Enabled) return false;
        lock (_lock)
        {
            if (_totalContextTokens > MaxContext)
            {
                int oldTokens = _totalContextTokens;
                _history.Clear();
                _totalContextTokens = _systemPromptTokens;
                _clearCount++;
                _nextKey = 1;   // 历史清空 → 编号键重置回 1
                LogHistoryCleared(oldTokens, "超过最大上下文");
                return true;
            }
            return false;
        }
    }

    /// <summary>统一的"历史清空"Info 日志。</summary>
    private void LogHistoryCleared(int oldTokens, string reason)
    {
        Logger.Info("历史清空: tokens=" + oldTokens + "/" + MaxContext + " " + reason + ", 清空次数=" + _clearCount);
    }

    /// <summary>记录 API 返回的精确 token 统计。</summary>
    public void RecordApiUsage(long promptTokens, long completionTokens)
    {
        if (promptTokens == 0 && completionTokens == 0) return;
        lock (_lock)
        {
            if (!_apiReturnsTokens)
                _apiReturnsTokens = true;
            _totalContextTokens = (int)(promptTokens + completionTokens);
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
                _totalContextTokens += (userInput.Length + assistantOutput.Length) * 3 / 4;
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

    /// <summary>强制清空历史（上下文接近上限但未触发自动清空时调用）。返回 true（始终清空）。</summary>
    public bool ClearHistory()
    {
        lock (_lock)
        {
            int oldTokens = _totalContextTokens;
            _history.Clear();
            _totalContextTokens = _systemPromptTokens;
            _clearCount++;
            _nextKey = 1;   // 历史清空 → 编号键重置回 1
            LogHistoryCleared(oldTokens, "已接近上限");
        }
        return true;
    }
}
