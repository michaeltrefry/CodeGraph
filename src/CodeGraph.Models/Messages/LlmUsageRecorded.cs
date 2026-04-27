namespace CodeGraph.Models.Messages;

public sealed class LlmUsageRecorded
{
    public string EventId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Path { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public DateTime CreatedAt { get; set; }
}
