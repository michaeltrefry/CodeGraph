namespace TC.CodeGraphApi.Models;

public record CrossRepoEdge
{
    public long Id { get; init; }
    public required string SourceProject { get; init; }
    public required string TargetProject { get; init; }
    public long SourceNodeId { get; init; }
    public long TargetNodeId { get; init; }
    public required EdgeType Type { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}
