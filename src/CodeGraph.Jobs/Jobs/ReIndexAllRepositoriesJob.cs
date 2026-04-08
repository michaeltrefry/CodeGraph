using CodeGraph.Services;

namespace CodeGraph.Jobs.Jobs;

public class ReIndexAllRepositoriesJob(
    IAdminService adminService) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var response = await adminService.ReIndexAllAsync();

        return new JobExecutionResult(
            Success: true,
            Message: $"Published {response.Count} repositories for re-indexing.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
