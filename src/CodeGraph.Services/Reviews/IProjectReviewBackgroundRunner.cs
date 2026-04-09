namespace CodeGraph.Services.Reviews;

public interface IProjectReviewBackgroundRunner
{
    Task EnqueueAsync(long reviewRunId, CancellationToken ct = default);
}
