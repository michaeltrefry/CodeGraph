using CodeGraph.Services.WikiRag;

namespace CodeGraph.Jobs.Jobs;

public class IngestConventionEmbeddingsJob(
    IConventionEmbeddingService conventionEmbeddingService) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var count = await conventionEmbeddingService.IngestAllAsync(ct);
        var completed = DateTime.UtcNow;

        return new JobExecutionResult(
            true,
            $"Indexed {count} convention chunks.",
            started,
            completed);
    }
}
