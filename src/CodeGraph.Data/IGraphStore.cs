using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>
/// Composite storage interface for the full code graph.
/// Extends focused sub-interfaces for analysis, metrics, and migrations.
/// Consumers that only need a subset should depend on the narrower interface.
/// </summary>
public interface IGraphStore : IGraphReader, IAnalysisStore, IMetricsStore, IReviewStore, IMigrationRunner
{
    Task<IAsyncDisposable> AcquireRepositoryIndexingLockAsync(
        string repository,
        CancellationToken ct = default);

    // Repositories
    Task UpsertRepositoryAsync(RepositoryEntity repository);
    /// <summary>
    /// Creates the repository row when it is missing (so graph writes can reference it).
    /// An existing row only has its identity fields refreshed; its index results
    /// (indexed_at, language, framework, properties) stay as the last completed run left them.
    /// </summary>
    Task EnsureRepositoryAsync(RepositoryEntity repository);
    Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync();
    Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25);
    Task<SchemaRepositorySearchResult> SearchSchemaRepositoriesAsync(
        string? search = null,
        string? server = null,
        string? database = null,
        int page = 1,
        int pageSize = 25);
    Task<IReadOnlyList<string>> GetDistinctGroupsAsync();
    Task<RepositoryInfo?> GetRepositoryByName(string name);
    Task UpdateRepositoryCommitShaAsync(string name, string? commitSha);
    Task DeleteRepositoryAsync(string repository);

    // Nodes
    Task<long> UpsertNodeAsync(GraphNode node);
    Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string repository, NodeLabel label, int limit = 10000);
    Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000);
    Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync();
    Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string repository);
    Task<Dictionary<string, int>> GetNodeCountsByLabelForRepositoryAsync(string repository);
    Task SetDoNotTrustAsync(long nodeId, bool doNotTrust);

    // Edges
    Task InsertEdgeAsync(GraphEdge edge);
    Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type);
    Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync();

    // Cross-repo link projections: stream only the linking keys, never whole rows of every repository.
    IAsyncEnumerable<LinkNodeRow> StreamLinkNodesAsync(LinkNodeQuery query, CancellationToken ct = default)
        => CrossRepoLinkProjection.StreamNodesAsync(this, query, ct);
    IAsyncEnumerable<LinkEdgeRow> StreamLinkEdgesAsync(LinkEdgeQuery query, CancellationToken ct = default)
        => CrossRepoLinkProjection.StreamEdgesAsync(this, query, ct);

    // Cross-repo edges
    /// <summary>Delete exactly the supplied persisted cross-repository edge IDs. Empty input
    /// is a no-op; callers use this for owned relation diffs without clearing unrelated links.</summary>
    Task DeleteCrossRepoEdgesByIdsAsync(IReadOnlyCollection<long> edgeIds, CancellationToken ct = default);
    Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge);
    Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyList<string>> FindRepositoriesWithNoCrossRepoEdgesAsync();
    Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync();

    // Aggregates
    /// <summary>
    /// The nodes most pointed at by <paramref name="repository"/>'s edges of <paramref name="edgeTypes"/>,
    /// computed over the whole graph: each node with its number of such incoming edges (at least
    /// <paramref name="minInDegree"/>), optionally restricted to <paramref name="targetLabels"/>,
    /// ordered by count descending then node id, at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<NodeInDegree>> GetTopInDegreeNodesAsync(
        string repository,
        IReadOnlyCollection<EdgeType> edgeTypes,
        IReadOnlyCollection<NodeLabel>? targetLabels,
        int minInDegree,
        int limit);

    // Bulk operations
    Task DeleteNodesByFileAsync(string repository, string filePath);
    Task DeleteNodesByRepositoryAsync(string repository);
    Task<int> ReplaceRepositoryGraphAsync(
        string repository,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        RepositoryEntity repositoryEntity,
        SyncStateEntity? syncState,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        CancellationToken ct = default);
    /// <summary>
    /// Atomically replaces the graph slice of <paramref name="filePaths"/>. When
    /// <paramref name="retractions"/> is given, its edges (matched by source, target and type) are
    /// deleted first and never preserved, and each of its orphan candidates without a file path is
    /// deleted once the write leaves it without edges.
    /// </summary>
    Task<int> ReplaceRepositoryFilesAsync(
        string repository,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        GraphSliceRetractions? retractions = null,
        CancellationToken ct = default);

    /// <summary>Nodes of <paramref name="repository"/> in any of <paramref name="filePaths"/>.</summary>
    Task<IReadOnlyList<GraphNode>> FindNodesByFilesAsync(
        string repository, IReadOnlyCollection<string> filePaths, CancellationToken ct = default);

    /// <summary>Distinct non-empty file paths of the nodes of <paramref name="repository"/>.</summary>
    Task<IReadOnlyList<string>> GetNodeFilePathsAsync(string repository, CancellationToken ct = default);

    /// <summary>Nodes of <paramref name="repository"/> whose name is one of <paramref name="names"/>.</summary>
    Task<IReadOnlyList<GraphNode>> FindNodesByNamesAsync(
        string repository, IReadOnlyCollection<string> names, CancellationToken ct = default);

    /// <summary>
    /// Edges of <paramref name="repository"/> of one of <paramref name="types"/> whose string property
    /// <paramref name="propertyKey"/> equals one of <paramref name="values"/>, with their endpoints'
    /// qualified names and files. The type predicate lets the store narrow the scan by edge type
    /// before it inspects edge properties.
    /// </summary>
    Task<IReadOnlyList<StoredEdge>> FindEdgesByPropertyAsync(
        string repository,
        IReadOnlyCollection<EdgeType> types,
        string propertyKey,
        IReadOnlyCollection<string> values,
        CancellationToken ct = default);
    Task DeleteFilesFromRepositoryGraphAsync(
        string repository,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default);

    // File hashes (incremental indexing)
    Task<Dictionary<string, string>> GetFileHashesAsync(string repository);

    /// <summary>Content hash and extraction version of every indexed file, keyed by relative path.</summary>
    Task<Dictionary<string, FileHashRecord>> GetFileHashRecordsAsync(string repository);
    Task UpsertFileHashBatchAsync(string repository, Dictionary<string, string> hashes, CancellationToken ct = default);
    Task DeleteFileHashesAsync(string repository, IReadOnlyList<string> relPaths);

    // Sync state
    Task<SyncStateEntity?> GetSyncStateAsync(string repository);
    Task<IReadOnlyDictionary<string, SyncStateEntity>> GetSyncStatesAsync(IReadOnlyList<string> repositories);
    Task UpsertSyncStateAsync(SyncStateEntity state);

    /// <summary>
    /// Records the vitals version a completed vitals computation used and clears the vitals
    /// failure record. No-op when the repository has no sync state.
    /// </summary>
    Task SetVitalsVersionAsync(string repository, int vitalsVersion);

    /// <summary>
    /// Counts a failed vitals computation of <paramref name="commitSha"/> at
    /// <paramref name="vitalsVersion"/>: consecutive with the stored record when both match,
    /// otherwise the first. Returns the consecutive count (0 when the repository has no sync state).
    /// </summary>
    Task<int> RecordVitalsFailureAsync(string repository, string? commitSha, int vitalsVersion);
    Task DeleteSyncStateAsync(string repository);

    // Clusters (community detection)
    Task ReplaceRepoClustersAsync(IReadOnlyList<RepoCluster> clusters);
    Task<IReadOnlyList<RepoCluster>> GetRepoClustersAsync(int level = 0);
    Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0);

    // Repository cleanup (cascading delete)
    Task DeleteAllEdgesForRepositoryAsync(string repository);
    Task DeleteCrossRepoEdgesForRepositoryAsync(string repository);
    Task DeleteAnalysisDataForRepositoryAsync(string repository);
}
