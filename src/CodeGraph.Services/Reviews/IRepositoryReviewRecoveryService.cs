namespace CodeGraph.Services.Reviews;

public interface IRepositoryReviewRecoveryService
{
    Task RecoverInterruptedRunsAsync(CancellationToken ct = default);
}
