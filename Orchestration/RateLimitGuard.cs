using System;


internal class RateLimitGuard
{
    private int _delayMs = 0;          // 当前退避延迟
    private int _cooldownStart = 0;    // Environment.TickCount when cooldown started

    private const int InitialDelayMs = 5000;
    private const int MaxDelayMs = 60000;

    /// <summary>限速时调用，启动/加长退避。</summary>
    public void OnRateLimited()
    {
        _delayMs = _delayMs == 0 ? InitialDelayMs : Math.Min(_delayMs * 2, MaxDelayMs);
        _cooldownStart = Environment.TickCount;
    }

    /// <summary>非限速错误时调用，重置退避。</summary>
    public void Reset()
    {
        _delayMs = 0;
        _cooldownStart = 0;
    }

    /// <summary>当前是否处于退避冷却期。</summary>
    public bool IsBlocked()
    {
        if (_delayMs == 0 || _cooldownStart == 0)
            return false;
        int elapsed = unchecked(Environment.TickCount - _cooldownStart);
        return elapsed < _delayMs;
    }

    /// <summary>当前退避延迟（日志用），单位毫秒。</summary>
    public int CurrentDelayMs => _delayMs;
}
