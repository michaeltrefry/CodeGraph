using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

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
    Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt);
    Task UpdateBatchRequestStatusAsync(string customId, string status, DateTime completedAt);
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
}
