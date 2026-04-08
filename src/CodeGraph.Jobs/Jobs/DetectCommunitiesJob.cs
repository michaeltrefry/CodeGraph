using CodeGraph.Services;

namespace CodeGraph.Jobs.Jobs;

public class DetectCommunitiesJob(
    IAdminService adminService) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        await adminService.DetectCommunitiesAsync(ct);

        return new JobExecutionResult(
            Success: true,
            Message: "Community detection completed.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
