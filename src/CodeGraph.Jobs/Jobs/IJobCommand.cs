namespace CodeGraph.Jobs.Jobs;

public interface IJobCommand<in TRequest>
{
    Task<JobExecutionResult> ExecuteAsync(TRequest request, CancellationToken ct = default);
}
