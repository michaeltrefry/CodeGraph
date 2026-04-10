using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>
/// Composite storage interface for the full code graph.
/// Extends focused sub-interfaces for analysis, metrics, and migrations.
/// Consumers that only need a subset should depend on the narrower interface.
/// </summary>
public interface IGraphStore : IAnalysisStore, IMetricsStore, IReviewStore, IMigrationRunner
{
    // Repositories
    Task UpsertRepositoryAsync(RepositoryEntity repository);
    Task<IReadOnlyList<ProjectInfo>> ListRepositoriesAsync();
    Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25);
    Task<IReadOnlyList<string>> GetDistinctGroupsAsync();
    Task<ProjectInfo?> GetRepositoryByName(string name);
    Task UpdateRepositoryCommitShaAsync(string name, string? commitSha);
    Task DeleteRepositoryAsync(string project);

    // Nodes
    Task<long> UpsertNodeAsync(GraphNode node);
    Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes, CancellationToken ct = default);
    Task<GraphNode?> FindNodeByIdAsync(long id);
    Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName);
    Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name, int limit = 1000);
    Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label, int limit = 10000);
    Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath, int limit = 5000);
    Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null,
        int limit = 50, int offset = 0, string? dotnetProject = null);
    Task<int> SearchNodesCountAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null, string? dotnetProject = null);
    Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000);
    Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync();
    Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids);
    Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string project);
    Task<Dictionary<string, int>> GetNodeCountsByLabelForProjectAsync(string project);
    Task SetDoNotTrustAsync(long nodeId, bool doNotTrust);

    // Edges
    Task InsertEdgeAsync(GraphEdge edge);
    Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null);
    Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null);
    Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(IReadOnlyList<long> targetIds, EdgeType[]? types = null);
    Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type);
    Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync();

    // Cross-repo edges
    Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge);
    Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(
        string project, EdgeType? type = null);
    Task<IReadOnlyList<string>> FindProjectsWithNoCrossRepoEdgesAsync();
    Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync();

    // Traversal
    Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
        TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0);
    Task<Dictionary<long, int>> GetCallFanInAsync(string project, int minFanIn);

    // Bulk operations
    Task DeleteNodesByFileAsync(string project, string filePath);
    Task DeleteNodesByProjectAsync(string project);

    // File hashes (incremental indexing)
    Task<Dictionary<string, string>> GetFileHashesAsync(string project);
    Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes, CancellationToken ct = default);
    Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths);

    // Sync state
    Task<SyncStateEntity?> GetSyncStateAsync(string project);
    Task<IReadOnlyDictionary<string, SyncStateEntity>> GetSyncStatesAsync(IReadOnlyList<string> projects);
    Task UpsertSyncStateAsync(SyncStateEntity state);
    Task DeleteSyncStateAsync(string project);

    // Clusters (community detection)
    Task ReplaceRepoClustersAsync(IReadOnlyList<RepoCluster> clusters);
    Task<IReadOnlyList<RepoCluster>> GetRepoClustersAsync(int level = 0);
    Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0);

    // Project cleanup (cascading delete)
    Task DeleteAllEdgesForProjectAsync(string project);
    Task DeleteCrossRepoEdgesForProjectAsync(string project);
    Task DeleteAnalysisDataForProjectAsync(string project);
}
