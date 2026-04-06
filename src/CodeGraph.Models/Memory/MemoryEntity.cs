namespace CodeGraph.Models.Memory;

public class MemoryEntity
{
    public required string Id { get; set; }
    public required string Label { get; set; }
    public required string Type { get; set; }
    public required string Summary { get; set; }
    public required string Source { get; set; }
    public float[]? Embedding { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
