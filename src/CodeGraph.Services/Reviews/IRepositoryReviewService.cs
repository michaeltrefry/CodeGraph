using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Reviews;

public interface IRepositoryReviewService
{
    Task<long> StartReviewAsync(string repo, string mode, CancellationToken ct = default);
    Task ExecuteReviewRunAsync(long reviewRunId, CancellationToken ct = default);
    Task<RepositoryReviewResponse?> GetReviewAsync(long reviewRunId, CancellationToken ct = default);
    Task<RepositoryReviewResponse?> GetLatestReviewAsync(string repo, CancellationToken ct = default);
}
