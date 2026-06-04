using System;
using System.Collections.Generic;
using System.Threading;


internal class TaskQueue
{
    private readonly Queue<TranslationTask> _queue = new Queue<TranslationTask>();
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);
    private readonly object _lock = new object();
    private readonly int _maxSize;

    private int _waitingTotalChars = 0;
    private int _outstandingCount = 0;    // 队列中+处理中的总数

    public int Count { get { lock (_lock) return _queue.Count; } }
    public int WaitingTotalChars { get { lock (_lock) return _waitingTotalChars; } }
    public int OutstandingCount { get { return _outstandingCount; } } // volatile read OK
    public AutoResetEvent Signal => _signal;

    public TaskQueue(int maxSize = 2000)
    {
        _maxSize = maxSize;
    }

    /// <summary>入队。队列满时返回 false，任务未被入队。</summary>
    public bool TryEnqueue(TranslationTask task)
    {
        lock (_lock)
        {
            if (_outstandingCount >= _maxSize)
                return false;
            _queue.Enqueue(task);
            _waitingTotalChars += task.CharLen;
            _outstandingCount++;
        }
        _signal.Set();
        return true;
    }

    /// <summary>
    /// 从队列头部取一批任务。
    /// 规则：不混搭重试/非重试任务，retryCount>2 单独成批，字数超限时截断但至少保留 1 条。
    /// 返回的 batch 从队列中移除，_waitingTotalChars 同步扣减。
    /// </summary>
    public List<TranslationTask> DequeueBatch(int maxChars)
    {
        var batch = new List<TranslationTask>();
        int totalChars = 0;
        lock (_lock)
        {
            int count = _queue.Count;
            while (count > 0)
            {
                var task = _queue.Peek();

                // 不混搭规则
                if (batch.Count > 0)
                {
                    if ((batch[0].RetryCount > 0) != (task.RetryCount > 0))
                        break;
                    if (task.RetryCount > 2)
                        break;
                }

                // 字数上限（至少保1条）
                if (totalChars + task.CharLen > maxChars && batch.Count > 0)
                    break;

                _queue.Dequeue();
                batch.Add(task);
                totalChars += task.CharLen;
                count--;
            }
            if (batch.Count > 0)
                _waitingTotalChars -= totalChars;
        }
        return batch;
    }

    /// <summary>重试时重新入队（不增加 _outstandingCount）。</summary>
    public void ReEnqueue(TranslationTask task)
    {
        lock (_lock)
        {
            task.ResetForRetry();
            _queue.Enqueue(task);
            _waitingTotalChars += task.CharLen;
        }
        _signal.Set();
    }

    /// <summary>任务完成时调用，递减 _outstandingCount。</summary>
    public void MarkCompleted()
    {
        Interlocked.Decrement(ref _outstandingCount);
    }
}
