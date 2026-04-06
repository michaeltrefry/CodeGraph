namespace CodeGraph.Models.Memory;

public class MemoryRelationship
{
    public required string FromId { get; set; }
    public required string ToId { get; set; }
    public required string RelationshipType { get; set; }
    public string? Context { get; set; }
    public required string Source { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? Supersedes { get; set; }
    public float[]? Embedding { get; set; }
}
