namespace CodeGraph.Data;

/// <summary>
/// Storage operations for codebase health metrics: file-level metrics,
/// repository health summaries, and AI-generated health analyses.
/// </summary>
public interface IMetricsStore
{
    // File metrics (vitals)
    Task UpsertFileMetricsBatchAsync(string repository, IReadOnlyList<FileMetricsEntity> metrics);
    Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(string repository, string? dotnetProject = null);
    /// <summary>The metrics row for one repo-relative file path (exact, case-sensitive match), or null.</summary>
    Task<FileMetricsEntity?> GetFileMetricAsync(string repository, string filePath);
    Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string repository, int top = 10);
    Task DeleteFileMetricsAsync(string repository);

    // Repository health summaries
    Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary);
    Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string repository);
    Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync();

    // Repository health analyses (Claude-generated)
    Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis);
    Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string repository);

    // Security findings
    Task DeleteSecurityFindingsAsync(string repository);
    Task UpsertSecurityFindingsBatchAsync(string repository, IReadOnlyList<SecurityFindingEntity> findings);
    Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string repository);
    Task UpsertRepositorySecuritySummaryAsync(RepositorySecuritySummaryEntity summary);
    Task<RepositorySecuritySummaryEntity?> GetRepositorySecuritySummaryAsync(string repository);
}
