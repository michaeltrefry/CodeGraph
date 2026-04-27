namespace CodeGraph.Models.Messages;

public sealed class McpToolInvocationRecorded
{
    public string EventId { get; set; } = "";
    public string? Username { get; set; }
    public long? TokenId { get; set; }
    public string ToolName { get; set; } = "";
    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime CreatedAt { get; set; }
}
