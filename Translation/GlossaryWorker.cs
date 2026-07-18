using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;


/// <summary>
/// 术语抽取线程：与翻译线程并行运行的独立长寿命后台线程。
///
/// 设计要点（与翻译线程解耦、无共享可变状态）：
///   - 翻译线程每完成一批译文即通过 <see cref="EnqueueSources"/> 把本批原文单向投递给本线程，
///     除此之外两条线程不共享可变状态。LLM 对话历史（ConversationHistory）已禁用，
///     两条线程各自的 LLM 调用彼此无状态依赖，杜绝同步问题。
///   - 本线程维护一份<b>本地最近原文环形缓冲</b>（RecentSourceBuffer，与 LLM 历史无关、
///     无前缀缓存/同步耦合），仅供模型做跨句专名识别；该缓冲随游戏文本推进滚动更新。
///   - 术语从 _pendingNew → _glossary 的晋升<b>只在本线程</b>按阈值触发
///     （<see cref="GlossaryManager.AddPendingTerms"/> 即时落盘 + 阈值 <see cref="GlossaryManager.MergePending"/>），
///     不再依赖对话历史清空事件，单点驱动杜绝竞态晋升。合并后下一批翻译线程派发时从
///     _glossary 重建系统提示词即传播（延迟 = 一批）。
///   - 限速退避与翻译线程共享同一个 <see cref="RateLimitGuard"/>（已加锁）：任一撞 429 双方共同退避。
///
/// 输出契约：系统提示词为术语抽取模板（{{术语表}} 由 GlossaryManager 填充、本体稳定利于前缀缓存），
/// user 消息携带最近原文，模型仅输出 {"glossary":{"原文":"译文"}}。
/// </summary>
internal class GlossaryWorker
{
    private readonly AutoLLMConfig _config;
    private readonly ILlmClient _llmClient;
    private readonly GlossaryManager _glossary;
    private readonly RateLimitGuard _rateLimitGuard;

    private readonly object _queueLock = new object();
    private readonly Queue<List<string>> _sourceQueue = new Queue<List<string>>();

    private readonly object _recentLock = new object();
    private readonly Queue<string> _recentBuffer;
    private readonly int _recentCapacity;

    private readonly AutoResetEvent _signal = new AutoResetEvent(false);
    private volatile bool _shutdownRequested;
    private Thread? _thread;
    private int _extractSeq;

    public GlossaryWorker(AutoLLMConfig config, ILlmClient llmClient,
        GlossaryManager glossary, RateLimitGuard rateLimitGuard)
    {
        _config = config;
        _llmClient = llmClient;
        _glossary = glossary;
        _rateLimitGuard = rateLimitGuard;
        _recentCapacity = config.GlossaryContextLines > 0 ? config.GlossaryContextLines : 50;
        _recentBuffer = new Queue<string>(_recentCapacity);
    }

    /// <summary>启动后台线程。</summary>
    public void Start()
    {
        _thread = new Thread(WorkerLoop) { IsBackground = true };
        _thread.Start();
    }

    /// <summary>请求关闭并唤醒线程使其退出。</summary>
    public void Shutdown()
    {
        _shutdownRequested = true;
        try { _signal.Set(); } catch { }
    }

    /// <summary>
    /// 投递本批原文给术语抽取线程。翻译线程在派发完成后调用。
    /// 同时把每条原文滚动写入最近原文环形缓冲（仅取每批前若干条以避免单批极端长文本撑爆缓冲）。
    /// </summary>
    public void EnqueueSources(List<string> sources)
    {
        if (_shutdownRequested) return;
        if (sources == null || sources.Count == 0) return;

        lock (_queueLock)
        {
            // 上限保护：避免翻译线程暴喂导致术语队列无限增长（丢弃最旧的一整批）
            if (_sourceQueue.Count > 64)
                _sourceQueue.Dequeue();
            _sourceQueue.Enqueue(sources);
        }

        // 写入最近原文环形缓冲：每条原文单独入列，超过容量丢最旧
        lock (_recentLock)
        {
            foreach (var s in sources)
            {
                if (string.IsNullOrEmpty(s)) continue;
                _recentBuffer.Enqueue(s);
                while (_recentBuffer.Count > _recentCapacity)
                    _recentBuffer.Dequeue();
            }
        }

        try { _signal.Set(); } catch { }
    }

    // ---- 工作线程主循环 ----

    private void WorkerLoop()
    {
        while (!_shutdownRequested)
        {
            _signal.WaitOne(100);

            if (_shutdownRequested) break;

            // 共享限速退避：与翻译线程共用同一 RateLimitGuard，任一撞 429 双方共同退避
            if (_rateLimitGuard.IsBlocked())
                continue;

            // 攒批：GlossaryBatchMerge=true 时把队列内全部待处理批合并成一次抽取调用（摊薄）
            List<List<string>>? batches = null;
            List<string>? single = null;
            lock (_queueLock)
            {
                if (_sourceQueue.Count == 0) continue;
                if (_config.GlossaryBatchMerge)
                {
                    batches = new List<List<string>>(_sourceQueue.Count);
                    while (_sourceQueue.Count > 0)
                        batches.Add(_sourceQueue.Dequeue());
                }
                else
                {
                    single = _sourceQueue.Dequeue();
                }
            }

            try
            {
                ProcessExtraction(batches, single);
            }
            catch (Exception ex)
            {
                Logger.Warn("术语抽取异常: " + ex.Message);
            }
        }
    }

    private void ProcessExtraction(List<List<string>>? batches, List<string>? single)
    {
        int seq = System.Threading.Interlocked.Increment(ref _extractSeq);

        // 合并本次要抽取的全部原文条目
        var allSources = new List<string>();
        if (batches != null)
        {
            foreach (var b in batches)
                allSources.AddRange(b);
        }
        if (single != null)
            allSources.AddRange(single);

        if (allSources.Count == 0) return;

        // 渲染最近原文（user 消息）：优先用本次全部原文，不足容量时补充缓冲中的更早行
        string recentText = RenderRecent(allSources);

        // 系统提示词：术语抽取模板（{{术语表}} 由 GlossaryManager.BuildSystemPrompt 填充，
        // 本体保持稳定以利于前缀缓存）
        string systemPrompt = _glossary.BuildSystemPrompt(_config.CachedExtractionPrompt);

        var messages = new List<LlmMessage>(2);
        messages.Add(new LlmMessage { Role = "system", Content = systemPrompt });
        messages.Add(new LlmMessage { Role = "user", Content = recentText });

        int started = Environment.TickCount;

        LlmResult result;
        try
        {
            result = _llmClient.Translate(
                _config.EndpointUrl, _config.ApiKey, _config.Model,
                messages, _config.ParsedModelParams);
        }
        catch (Exception ex)
        {
            // 限速错误（429）经 RateLimitGuard 处理；其余错误：把本轮 sources 退回队首稍后重试
            HandleCallFailure(ex, batches, single);
            return;
        }

        _rateLimitGuard.Reset();

        int elapsed = unchecked(Environment.TickCount - started);

        // 解析 {"glossary":{...}}
        Dictionary<string, object>? glossaryObj = null;
        if (!string.IsNullOrEmpty(result.FullResponse))
        {
            try
            {
                var obj = SimpleJson.ParseJsonObject(result.FullResponse);
                if (obj != null
                    && obj.TryGetValue("glossary", out object? gObj)
                    && gObj is Dictionary<string, object> gDict)
                    glossaryObj = gDict;
            }
            catch (Exception ex)
            {
                Logger.Warn("术语抽取响应解析失败 [seq " + seq + "]: " + ex.Message +
                    " | 原始: " + Truncate(result.FullResponse, 200));
            }
        }
        else
        {
            Logger.Warn("术语抽取响应为空 [seq " + seq + "]");
        }

        // 收集新术语（AddPendingTerms 即时落盘，防止游戏意外停止丢失）
        if (glossaryObj != null && glossaryObj.Count > 0)
            _glossary.AddPendingTerms(glossaryObj);

        // 阈值触发晋升 _pendingNew → _glossary（合并内存，文件已落盘）
        // 阈值为 0 表示不自动晋升（仅靠落盘文件语义），通常用默认 3
        int pending = _glossary.PendingCount;
        bool merged = false;
        if (_config.GlossaryMergeThreshold > 0
            && pending >= _config.GlossaryMergeThreshold)
        {
            int added = _glossary.MergePending();
            merged = added > 0;
        }

        // 轨迹日志（Debug）
        {
            int newCount = glossaryObj != null ? glossaryObj.Count : 0;
            var trace = new StringBuilder();
            trace.Append("[术语抽取] seq").Append(seq)
                 .Append(" 输入").Append(allSources.Count).Append("条")
                 .Append(" 耗时").Append(elapsed).Append("ms")
                 .Append(" 输出").Append(result.Usage?.CompletionTokens ?? 0).Append("tokens");
            if (LlmClient.CacheStatsSupported)
            {
                trace.Append(" [缓存命中").Append(result.Usage?.CacheHitTokens ?? 0)
                     .Append(" 未中").Append(result.Usage?.CacheMissTokens ?? 0).Append("]");
            }
            trace.Append(" 新术语").Append(newCount)
                 .Append(" 待合并").Append(pending)
                 .Append(merged ? " [已触发合并]" : "");
            if (!string.IsNullOrEmpty(result.Reasoning))
                trace.Append("\n  思考: ").Append(Flatten(Truncate(result.Reasoning, 400)));
            trace.Append("\n  输出: ").Append(Flatten(Truncate(result.FullResponse, 400)));
            Logger.Debug(trace.ToString());
        }
    }

    /// <summary>
    /// 渲染最近原文为 user 消息内容。优先包含本轮待抽取的全部原文；
    /// 若总条数少于缓冲容量，从最近原文缓冲中回填更早的行（最多到容量），提供跨句上下文。
    /// </summary>
    private string RenderRecent(List<string> currentSources)
    {
        var lines = new List<string>(currentSources);

        // 从最近原文缓冲快照一份（锁内拷贝），按时间倒序最近在前，我们用正序浏览
        List<string> snapshot;
        lock (_recentLock)
        {
            snapshot = new List<string>(_recentBuffer);
        }

        if (lines.Count < _recentCapacity && snapshot.Count > 0)
        {
            // 回填更早的行：缓冲中不属本轮的行（出现在 snapshots 中靠后的部分）
            // 简单做法：取缓冲最后 _recentCapacity - lines.Count 行（更早的），放在 lines 前
            int need = _recentCapacity - lines.Count;
            int start = Math.Max(0, snapshot.Count - need);
            var prefix = new List<string>(need);
            for (int i = start; i < snapshot.Count; i++)
            {
                var s = snapshot[i];
                // 跳过本轮已包含的（避免重复），简单按引用相等
                if (currentSources.Contains(s)) continue;
                prefix.Add(s);
            }
            lines.InsertRange(0, prefix);
        }

        // net35 无 string.Join(string, IEnumerable<string>) 重载，先转 string[]
        return string.Join("\n", lines.ToArray());
    }

    /// <summary>处理 LLM 调用失败：限速则共享退避并把本轮 sources 退回队首；其余错误丢弃（尽力而为）。</summary>
    private void HandleCallFailure(Exception ex, List<List<string>>? batches, List<string>? single)
    {
        if (ex is System.Net.WebException we && we.Response is System.Net.HttpWebResponse resp
            && (int)resp.StatusCode == 429)
        {
            _rateLimitGuard.OnRateLimited();
            Logger.Info("术语抽取限速退避: " + (_rateLimitGuard.CurrentDelayMs / 1000) + " 秒");

            // 把本轮 sources 退回队首稍后重试
            lock (_queueLock)
            {
                if (batches != null)
                {
                    // 退回顺序：保持原入队顺序（batches[0] 最先入队）
                    for (int i = batches.Count - 1; i >= 0; i--)
                        EnqueueFront(batches[i]);
                }
                if (single != null)
                    EnqueueFront(single);
            }
        }
        else
        {
            // 非限速错误：术语抽取为尽力而为，丢弃本轮，下一批翻译会再投递新原文
            Logger.Warn("术语抽取调用失败，丢弃本轮（尽力而为）: " + ex.Message);
        }
    }

    /// <summary>把一批原文退回队列<b>队首</b>（需在 _queueLock 内调用）。</summary>
    private void EnqueueFront(List<string> sources)
    {
        // Queue 不支持队首入列；用临时数组重建
        var tmp = new List<List<string>>(_sourceQueue.Count + 1);
        tmp.Add(sources);
        while (_sourceQueue.Count > 0)
            tmp.Add(_sourceQueue.Dequeue());
        foreach (var x in tmp)
            _sourceQueue.Enqueue(x);
    }

    // ---- 辅助 ----

    private static string Flatten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text!.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxLen) return text;
        return text.Substring(0, maxLen) + "...";
    }
}