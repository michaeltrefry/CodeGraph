using CodeGraph.Services;

namespace CodeGraph.Jobs.Jobs;

public class LinkAndDetectJob(
    IAdminService adminService) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        await adminService.LinkAndDetectAsync(ct);

        return new JobExecutionResult(
            Success: true,
            Message: "Cross-repo linking and community detection completed.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
