using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>
/// Storage operations for AI analysis results: summaries, project analyses,
/// batch tracking, node analysis, and graph context queries for prompt building.
/// </summary>
public interface IAnalysisStore
{
    // Summaries
    Task UpsertRepositorySummaryAsync(string project, string summary,
        ConfidenceLevel confidence, string sourceHash, string? modelUsed = null);
    Task<ProjectSummary?> GetRepositorySummaryAsync(string project);

    // Per-project analyses
    Task UpsertProjectAnalysisAsync(string repo, StoredProjectAnalysis analysis);
    Task<IReadOnlyList<StoredProjectAnalysis>> GetProjectAnalysesAsync(string repo);

    // Analysis batch tracking
    Task<long> CreateAnalysisBatchAsync(AnalysisBatchEntity batch);
    Task CreateBatchRequestsAsync(IEnumerable<AnalysisBatchRequestEntity> requests);
    Task<IReadOnlyList<StoredAnalysisBatch>> GetPendingBatchesAsync(string? repo = null);
    Task<StoredAnalysisBatch?> GetLatestBatchAsync(string repo);
    Task<StoredAnalysisBatch?> GetBatchByProviderBatchIdAsync(string providerBatchId);
    Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt);
    Task UpdateBatchRequestStateAsync(long batchId, string customId, string status, int attemptCount,
        string? responseText, string? modelUsed, DateTime? completedAt);
    Task<IReadOnlyList<AnalysisBatchRequestEntity>> GetBatchRequestsAsync(long batchId);

    // Node analysis results
    Task UpsertNodeAnalysisAsync(NodeAnalysisEntity analysis);
    Task<StoredNodeAnalysis?> GetNodeAnalysisAsync(long nodeId);
    Task<Dictionary<long, StoredNodeAnalysis>> GetNodeAnalysesBatchAsync(IReadOnlyList<long> nodeIds);

    // Graph context for batch prompt building
    Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string project);
    Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId);
    Task<IReadOnlyList<EdgeEntity>> GetOutboundEdgesAsync(long nodeId);
    Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId);
    Task<IReadOnlyList<NodeEntity>> GetAllNodesByProjectAsync(string project);
    Task<IReadOnlyList<EdgeEntity>> GetAllEdgesByProjectAsync(string project);
    Task<IReadOnlyList<EdgeEntity>> GetEdgesForNodesAsync(IReadOnlyList<long> nodeIds);

    /// <summary>
    /// Edges whose source or target node belongs to <paramref name="project"/>, without their
    /// properties (<see cref="EdgeEntity.Properties"/> is null). Same edge set as
    /// <see cref="GetEdgesForNodesAsync"/> over every node of the project, at a fraction of the memory.
    /// </summary>
    async Task<IReadOnlyList<EdgeEntity>> GetEdgeEndpointsForProjectAsync(string project)
    {
        var nodeIds = (await GetAllNodesByProjectAsync(project)).Select(node => node.Id).ToList();
        return (await GetEdgesForNodesAsync(nodeIds))
            .Select(edge => new EdgeEntity
            {
                Id = edge.Id, Project = edge.Project, SourceId = edge.SourceId, TargetId = edge.TargetId, Type = edge.Type
            })
            .ToList();
    }

    /// <summary>
    /// The project's File nodes plus every node flagged do-not-trust, without properties
    /// (<see cref="NodeEntity.Properties"/> is null) — what vitals needs, not the whole graph.
    /// </summary>
    async Task<IReadOnlyList<NodeEntity>> GetFileAndUntrustedNodesAsync(string project) =>
        (await GetAllNodesByProjectAsync(project))
            .Where(node => node.Label == "File" || node.DoNotTrust)
            .Select(node => new NodeEntity
            {
                Id = node.Id, Project = node.Project, DotnetProject = node.DotnetProject, Label = node.Label,
                Name = node.Name, QualifiedName = node.QualifiedName, FilePath = node.FilePath,
                StartLine = node.StartLine, EndLine = node.EndLine, DoNotTrust = node.DoNotTrust
            })
            .ToList();
}
