using System;


internal class RateLimitGuard
{
    private readonly object _lock = new object();
    private int _delayMs = 0;          // 当前退避延迟
    private int _cooldownStart = 0;    // Environment.TickCount when cooldown started

    private const int InitialDelayMs = 5000;
    private const int MaxDelayMs = 60000;

    // 双线程架构（翻译线程 + 术语抽取线程）共享同一限速状态：任一撞 429 双方共同退避，
    // 避免两个线程交替撞墙。所有读写均在 _lock 内，保证两线程观察到的退避状态一致。

    /// <summary>限速时调用，启动/加长退避。</summary>
    public void OnRateLimited()
    {
        lock (_lock)
        {
            _delayMs = _delayMs == 0 ? InitialDelayMs : Math.Min(_delayMs * 2, MaxDelayMs);
            _cooldownStart = Environment.TickCount;
        }
    }

    /// <summary>非限速错误时调用，重置退避。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _delayMs = 0;
            _cooldownStart = 0;
        }
    }

    /// <summary>当前是否处于退避冷却期。</summary>
    public bool IsBlocked()
    {
        lock (_lock)
        {
            if (_delayMs == 0 || _cooldownStart == 0)
                return false;
            int elapsed = unchecked(Environment.TickCount - _cooldownStart);
            return elapsed < _delayMs;
        }
    }

    /// <summary>当前退避延迟（日志用），单位毫秒。</summary>
    public int CurrentDelayMs { get { lock (_lock) return _delayMs; } }
}


internal class RetryHandler
{
    private readonly int _maxRetry;

    public RetryHandler(int maxRetry)
    {
        _maxRetry = maxRetry;
    }

    /// <summary>判断任务是否应重试。</summary>
    public bool ShouldRetry(TranslationTask task)
    {
        return task.RetryCount < _maxRetry;
    }

    /// <summary>递增重试计数（调用方在 ShouldRetry 返回 true 后调用）。</summary>
    public void IncrementRetry(TranslationTask task)
    {
        task.RetryCount++;
    }
}