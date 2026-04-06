namespace CodeGraph.Models.Responses;

public record GraphOverviewResponse(
    IReadOnlyList<GraphOverviewNode> Nodes,
    IReadOnlyList<GraphOverviewEdge> Edges);

public record GraphOverviewNode(
    string Name,
    string? GitLabGroup,
    string? Language,
    string? Framework,
    bool IsFoundational);

public record GraphOverviewEdge(
    string Source,
    string Target,
    int Count,
    Dictionary<string, int> TypeCounts);
