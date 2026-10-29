namespace CodeGraph.Models;

public record CrossRepoEdge
{
    public long Id { get; init; }
    public required string SourceRepository { get; init; }
    public required string TargetRepository { get; init; }
    public long SourceNodeId { get; init; }
    public long TargetNodeId { get; init; }
    public required EdgeType Type { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}
