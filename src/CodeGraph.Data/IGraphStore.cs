using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>
/// Composite storage interface for the full code graph.
/// Extends focused sub-interfaces for analysis, metrics, and migrations.
/// Consumers that only need a subset should depend on the narrower interface.
/// </summary>
public interface IGraphStore : IGraphReader, IAnalysisStore, IMetricsStore, IReviewStore, IMigrationRunner
{
    Task<IAsyncDisposable> AcquireProjectIndexingLockAsync(
        string project,
        CancellationToken ct = default);

    // Repositories
    Task UpsertRepositoryAsync(RepositoryEntity repository);
    /// <summary>
    /// Creates the repository row when it is missing (so graph writes can reference it).
    /// An existing row only has its identity fields refreshed; its index results
    /// (indexed_at, language, framework, properties) stay as the last completed run left them.
    /// </summary>
    Task EnsureRepositoryAsync(RepositoryEntity repository);
    Task<IReadOnlyList<ProjectInfo>> ListRepositoriesAsync();
    Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25);
    Task<SchemaRepositorySearchResult> SearchSchemaRepositoriesAsync(
        string? search = null,
        string? server = null,
        string? database = null,
        int page = 1,
        int pageSize = 25);
    Task<IReadOnlyList<string>> GetDistinctGroupsAsync();
    Task<ProjectInfo?> GetRepositoryByName(string name);
    Task UpdateRepositoryCommitShaAsync(string name, string? commitSha);
    Task DeleteRepositoryAsync(string project);

    // Nodes
    Task<long> UpsertNodeAsync(GraphNode node);
    Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label, int limit = 10000);
    Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000);
    Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync();
    Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string project);
    Task<Dictionary<string, int>> GetNodeCountsByLabelForProjectAsync(string project);
    Task SetDoNotTrustAsync(long nodeId, bool doNotTrust);

    // Edges
    Task InsertEdgeAsync(GraphEdge edge);
    Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type);
    Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync();

    // Cross-repo link projections: stream only the linking keys, never whole rows of every project.
    IAsyncEnumerable<LinkNodeRow> StreamLinkNodesAsync(LinkNodeQuery query, CancellationToken ct = default)
        => CrossRepoLinkProjection.StreamNodesAsync(this, query, ct);
    IAsyncEnumerable<LinkEdgeRow> StreamLinkEdgesAsync(LinkEdgeQuery query, CancellationToken ct = default)
        => CrossRepoLinkProjection.StreamEdgesAsync(this, query, ct);

    // Cross-repo edges
    Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge);
    Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyList<string>> FindProjectsWithNoCrossRepoEdgesAsync();
    Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync();

    // Traversal
    Task<Dictionary<long, int>> GetCallFanInAsync(string project, int minFanIn);

    // Bulk operations
    Task DeleteNodesByFileAsync(string project, string filePath);
    Task DeleteNodesByProjectAsync(string project);
    Task<int> ReplaceProjectGraphAsync(
        string project,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        RepositoryEntity repository,
        SyncStateEntity? syncState,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        CancellationToken ct = default);
    Task<int> ReplaceProjectFilesAsync(
        string project,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        CancellationToken ct = default);
    Task DeleteFilesFromProjectGraphAsync(
        string project,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default);

    // File hashes (incremental indexing)
    Task<Dictionary<string, string>> GetFileHashesAsync(string project);

    /// <summary>Content hash and extraction version of every indexed file, keyed by relative path.</summary>
    Task<Dictionary<string, FileHashRecord>> GetFileHashRecordsAsync(string project);
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
