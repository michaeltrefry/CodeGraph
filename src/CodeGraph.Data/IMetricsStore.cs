namespace CodeGraph.Data;

/// <summary>
/// Storage operations for codebase health metrics: file-level metrics,
/// project health summaries, and AI-generated health analyses.
/// </summary>
public interface IMetricsStore
{
    // File metrics (vitals)
    Task UpsertFileMetricsBatchAsync(string project, IReadOnlyList<FileMetricsEntity> metrics);
    Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(string project, string? dotnetProject = null);
    Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string project, int top = 10);
    Task DeleteFileMetricsAsync(string project);

    // Project health summaries
    Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary);
    Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string project);
    Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync();

    // Project health analyses (Claude-generated)
    Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis);
    Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string project);

    // Security findings
    Task DeleteSecurityFindingsAsync(string project);
    Task UpsertSecurityFindingsBatchAsync(string project, IReadOnlyList<SecurityFindingEntity> findings);
    Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string project);
    Task UpsertProjectSecuritySummaryAsync(ProjectSecuritySummaryEntity summary);
    Task<ProjectSecuritySummaryEntity?> GetProjectSecuritySummaryAsync(string project);
}
