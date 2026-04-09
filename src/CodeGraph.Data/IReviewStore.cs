namespace CodeGraph.Data;

/// <summary>
/// Storage operations for persisted project diagnostics and review runs.
/// </summary>
public interface IReviewStore
{
    Task DeleteProjectDiagnosticsAsync(string project);
    Task UpsertProjectDiagnosticsBatchAsync(string project, IReadOnlyList<ProjectDiagnosticEntity> diagnostics);
    Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(string project, string? dotnetProject = null);

    Task<long> CreateProjectReviewRunAsync(ProjectReviewRunEntity run);
    Task UpdateProjectReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null,
        DateTime? completedAt = null, string? error = null);
    Task UpsertProjectReviewFindingsAsync(long reviewRunId, IReadOnlyList<ProjectReviewFindingEntity> findings);
    Task<ProjectReviewRunEntity?> GetProjectReviewRunAsync(long reviewRunId);
    Task<ProjectReviewRunEntity?> GetLatestProjectReviewRunAsync(string project, string projectName);
    Task<IReadOnlyList<ProjectReviewFindingEntity>> GetProjectReviewFindingsAsync(long reviewRunId);
}
