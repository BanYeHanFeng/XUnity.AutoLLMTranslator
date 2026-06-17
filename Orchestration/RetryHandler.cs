#nullable disable
using System;


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
