namespace CodeGraph.Models.Responses;

public record NodeDetailResponse(
    GraphNode Node,
    IReadOnlyList<EdgeSummary> OutboundEdges,
    IReadOnlyList<EdgeSummary> InboundEdges,
    IReadOnlyList<CrossRepoEdgeSummary> CrossRepoEdges)
{
    /// <summary>The commit this answer describes (the default-branch index commit, or the requested SHA).</summary>
    public string? CommitSha { get; init; }
}

public record EdgeSummary(
    long EdgeId,
    string Type,
    long NeighborId,
    string? NeighborName,
    string? NeighborQualifiedName,
    string? NeighborLabel,
    string? NeighborProject,
    bool IsCrossProject,
    Dictionary<string, object> Properties);

public record NodeSourceResponse(
    string FilePath,
    int StartLine,
    int EndLine,
    string Content,
    string Language)
{
    /// <summary>The commit this answer describes (the default-branch index commit, or the requested SHA).</summary>
    public string? CommitSha { get; init; }
}

public record CrossRepoEdgeSummary(
    long EdgeId,
    string Type,
    string Direction,
    string SourceProject,
    string TargetProject,
    long NeighborNodeId,
    string? NeighborName,
    string? NeighborQualifiedName,
    string? NeighborLabel,
    Dictionary<string, object> Properties);
