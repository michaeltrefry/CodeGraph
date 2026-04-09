namespace CodeGraph.Services.Reviews;

public interface IRepositoryReviewBackgroundRunner
{
    Task EnqueueAsync(long reviewRunId, CancellationToken ct = default);
}
