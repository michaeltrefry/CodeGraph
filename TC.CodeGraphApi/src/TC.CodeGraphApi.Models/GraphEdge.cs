namespace TC.CodeGraphApi.Models;

public record GraphEdge
{
    public long Id { get; init; }
    public required string Project { get; init; }
    public long SourceId { get; init; }
    public long TargetId { get; init; }
    public required EdgeType Type { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}
