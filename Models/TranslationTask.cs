using System;


internal enum TaskState { Waiting, Processing, Completed, Failed }

internal class TranslationTask
{
    // 输入（创建时设置）
    public string UntranslatedText { get; set; } = "";

    // 输出（完成时设置，完成前为 null）
    public string? TranslatedText { get; set; }
    public string? ErrorMessage { get; set; }

    // 状态机
    public TaskState State { get; set; }
    public int RetryCount { get; set; }
    public int CharLen { get; set; }                         // UntranslatedText.Length

    // 时间戳
    public long CreatedTick { get; set; }                     // Environment.TickCount

    // 协程等待（endpoint 用此字段轮询完成状态）
    public volatile bool IsCompleted;

    // 便利方法
    public void MarkCompleted(string translated)
    {
        TranslatedText = translated;
        State = TaskState.Completed;
        IsCompleted = true;
    }

    public void MarkFailed(string error)
    {
        ErrorMessage = error;
        State = TaskState.Failed;
        IsCompleted = true;
    }

    public void ResetForRetry()
    {
        State = TaskState.Waiting;
        TranslatedText = null;
        ErrorMessage = null;
    }
}
