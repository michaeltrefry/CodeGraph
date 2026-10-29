namespace CodeGraph.Data;

/// <summary>
/// Storage operations for persisted repository diagnostics and review runs.
/// </summary>
public interface IReviewStore
{
    Task DeleteProjectDiagnosticsAsync(string repository);
    Task UpsertProjectDiagnosticsBatchAsync(string repository, IReadOnlyList<ProjectDiagnosticEntity> diagnostics);
    Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(string repository, string? dotnetProject = null);

    Task<long> CreateProjectReviewRunAsync(ProjectReviewRunEntity run);
    Task UpdateProjectReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null,
        DateTime? completedAt = null, string? error = null);
    Task UpsertProjectReviewFindingsAsync(long reviewRunId, IReadOnlyList<ProjectReviewFindingEntity> findings);
    Task<ProjectReviewRunEntity?> GetProjectReviewRunAsync(long reviewRunId);
    Task<ProjectReviewRunEntity?> GetLatestProjectReviewRunAsync(string repository, string projectName);
    Task<IReadOnlyList<ProjectReviewFindingEntity>> GetProjectReviewFindingsAsync(long reviewRunId);

    Task<long> CreateRepositoryReviewRunAsync(RepositoryReviewRunEntity run);
    Task UpdateRepositoryReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null,
        DateTime? completedAt = null, string? error = null);
    Task UpsertRepositoryReviewFindingsAsync(long reviewRunId, IReadOnlyList<RepositoryReviewFindingEntity> findings);
    Task UpsertRepositoryReviewProjectSectionsAsync(long reviewRunId,
        IReadOnlyList<RepositoryReviewProjectSectionEntity> sections);
    Task<RepositoryReviewRunEntity?> GetRepositoryReviewRunAsync(long reviewRunId);
    Task<RepositoryReviewRunEntity?> GetLatestRepositoryReviewRunAsync(string repo);
    Task<IReadOnlyList<RepositoryReviewRunEntity>> GetRepositoryReviewRunsByStatusAsync(IReadOnlyList<string> statuses);
    Task<IReadOnlyList<RepositoryReviewFindingEntity>> GetRepositoryReviewFindingsAsync(long reviewRunId);
    Task<IReadOnlyList<RepositoryReviewProjectSectionEntity>> GetRepositoryReviewProjectSectionsAsync(long reviewRunId);
}
