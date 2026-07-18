using System;
using System.Collections.Generic;


internal class LlmMessage
{
    public string Role { get; set; } = "";      // "system" | "user" | "assistant"
    public string Content { get; set; } = "";
}

internal class LlmResult
{
    public string FullResponse { get; set; } = "";
    public LlmUsage? Usage { get; set; }
    public int ChunkCount { get; set; }
    public bool DoneReceived { get; set; }
    public long ElapsedMs { get; set; }

    // 模型思考过程（reasoning_content），仅用于调用轨迹日志核实模型锚定/续写行为，
    // 不参与 token 统计与对话历史记录。无思考输出的模型为空字符串。
    public string Reasoning { get; set; } = "";
}

internal class LlmUsage
{
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
}


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

    // 本批 JSON 输入/输出键。输入/输出均采用同一编号（"1"/"2"/...）：每个条目在
    // ConversationHistory 中按全局单调递增分配，使跨批次历史译文与当前输入中重号
    // 条目不被模型误并；历史清空后重置回 1。负责任务对象的复用：失败/限速重试
    // 时沿用原键（ResetForRetry 不清除此字段），故重试批次的输入键与首次一致。
    public string UserKey { get; set; } = "";

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