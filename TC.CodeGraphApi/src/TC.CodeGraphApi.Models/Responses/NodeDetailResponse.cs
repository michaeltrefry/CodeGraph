namespace TC.CodeGraphApi.Models.Responses;

public record NodeDetailResponse(
    GraphNode Node,
    IReadOnlyList<EdgeSummary> OutboundEdges,
    IReadOnlyList<EdgeSummary> InboundEdges,
    IReadOnlyList<CrossRepoEdgeSummary> CrossRepoEdges);

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
    string Language);

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
