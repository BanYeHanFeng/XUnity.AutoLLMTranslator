using System;


internal class LlmMessage
{
    public string Role { get; set; }      // "system" | "user" | "assistant"
    public string Content { get; set; }
}

internal class LlmResult
{
    public string FullResponse { get; set; }
    public LlmUsage Usage { get; set; }
    public int ChunkCount { get; set; }
    public bool DoneReceived { get; set; }
    public long ElapsedMs { get; set; }
}

internal class LlmUsage
{
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
}
