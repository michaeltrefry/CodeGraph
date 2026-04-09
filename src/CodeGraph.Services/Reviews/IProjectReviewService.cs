using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Reviews;

public interface IProjectReviewService
{
    Task<long> StartReviewAsync(string repo, string projectName, string mode, CancellationToken ct = default);
    Task ExecuteReviewRunAsync(long reviewRunId, CancellationToken ct = default);
    Task<ProjectReviewResponse?> GetReviewAsync(long reviewRunId, CancellationToken ct = default);
    Task<ProjectReviewResponse?> GetLatestReviewAsync(string repo, string projectName, CancellationToken ct = default);
    Task<ProjectDiagnosticsResponse> GetDiagnosticsAsync(string repo, string? dotnetProject = null, CancellationToken ct = default);
}
