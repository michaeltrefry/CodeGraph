namespace TC.CodeGraphApi.Models.Responses;

public record ClusterOverviewResponse(
    IReadOnlyList<ClusterSummary> Clusters,
    double Modularity,
    int TotalProjects,
    int ClusteredProjects,
    DateTime? ComputedAt);

public record ClusterSummary(
    int ClusterId,
    string? Label,
    IReadOnlyList<string> Members,
    int InternalEdgeCount,
    int ExternalEdgeCount,
    double Density,
    IReadOnlyList<string> BridgeRepos);

public record ClusterDetailResponse(
    int ClusterId,
    string? Label,
    IReadOnlyList<ClusterMember> Members,
    int InternalEdgeCount,
    int ExternalEdgeCount,
    IReadOnlyList<ClusterConnection> TopConnections);

public record ClusterMember(
    string ProjectName,
    decimal BetweennessCentrality,
    int InternalEdges,
    int ExternalEdges);

public record ClusterConnection(
    int TargetClusterId,
    string? TargetLabel,
    int EdgeCount,
    IReadOnlyList<string> EdgeTypes);

public record ClusterGraphResponse(
    IReadOnlyList<ClusterGraphNode> Nodes,
    IReadOnlyList<ClusterGraphEdge> Edges,
    IReadOnlyList<ClusterInfo> Clusters,
    double Modularity);

public record ClusterGraphNode(
    string Name,
    string? GitLabGroup,
    string? Language,
    string? Framework,
    bool IsFoundational,
    int? ClusterId,
    decimal BetweennessCentrality);

public record ClusterGraphEdge(
    string Source,
    string Target,
    int Count,
    Dictionary<string, int> TypeCounts,
    bool IsCrossCluster);

public record ClusterInfo(
    int ClusterId,
    string? Label,
    int MemberCount);
