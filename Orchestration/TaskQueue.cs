using System;
using System.Collections.Generic;
using System.Threading;


internal class TaskQueue
{
    private readonly List<TranslationTask> _list = new List<TranslationTask>();
    private int _head = 0;
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);
    private readonly object _lock = new object();
    private readonly int _maxSize;

    private int _waitingTotalChars = 0;
    private int _outstandingCount = 0;

    public int Count { get { lock (_lock) return _list.Count - _head; } }
    public int WaitingTotalChars { get { lock (_lock) return _waitingTotalChars; } }
    public int OutstandingCount { get { return _outstandingCount; } }
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
            _list.Add(task);
            _waitingTotalChars += task.CharLen;
            _outstandingCount++;
        }
        _signal.Set();
        return true;
    }

    /// <summary>
    /// 从队首取出一组兼容任务（不混搭重试/非重试，retryCount>2 单独成批）。
    /// 无字符数上限——批次大小由外部 MaxContext 控制。
    /// </summary>
    public List<TranslationTask> DequeueAll()
    {
        var batch = new List<TranslationTask>();
        lock (_lock)
        {
            CompactIfNeeded();
            int totalChars = 0;
            while (_head < _list.Count)
            {
                var task = _list[_head];
                if (batch.Count > 0)
                {
                    if ((batch[0].RetryCount > 0) != (task.RetryCount > 0))
                        break;
                    if (task.RetryCount > 2)
                        break;
                }
                _head++;
                batch.Add(task);
                totalChars += task.CharLen;
            }
            _waitingTotalChars -= totalChars;
        }
        return batch;
    }

    /// <summary>
    /// 将一组任务插入队首，保持传入列表顺序。
    /// 用于 overflow 任务归位——它们在原始队列中位于已取走批次的后面，
    /// 应优先于新到达的任务被处理。
    /// </summary>
    public void ReEnqueueFront(List<TranslationTask> tasks)
    {
        if (tasks == null || tasks.Count == 0) return;
        lock (_lock)
        {
            CompactIfNeeded();
            _list.InsertRange(_head, tasks);
            int totalChars = 0;
            foreach (var t in tasks) totalChars += t.CharLen;
            _waitingTotalChars += totalChars;
        }
        _signal.Set();
    }

    /// <summary>重试时重新入队（不增加 _outstandingCount）。</summary>
    public void ReEnqueue(TranslationTask task)
    {
        lock (_lock)
        {
            task.ResetForRetry();
            _list.Add(task);
            _waitingTotalChars += task.CharLen;
        }
        _signal.Set();
    }

    /// <summary>任务完成时调用，递减 _outstandingCount。</summary>
    public void MarkCompleted()
    {
        Interlocked.Decrement(ref _outstandingCount);
    }

    /// <summary>当 _head 超过容量一半时，将有效元素前移并重置 _head。</summary>
    private void CompactIfNeeded()
    {
        int remaining = _list.Count - _head;
        if (_head > _list.Count / 2 && remaining > 0)
        {
            _list.RemoveRange(0, _head);
            _head = 0;
        }
    }
}
