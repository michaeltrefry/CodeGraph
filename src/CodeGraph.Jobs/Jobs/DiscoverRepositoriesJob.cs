using CodeGraph.Models.Requests;
using CodeGraph.Services;

namespace CodeGraph.Jobs.Jobs;

public class DiscoverRepositoriesJob(
    IAdminService adminService,
    ILogger<DiscoverRepositoriesJob> logger) : IJobCommand<DiscoverRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(DiscoverRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var response = await adminService.DiscoverAsync(request);
        logger.LogInformation(
            "Discovered {NewProjects} new projects. Skipped {SkippedProjects} existing. Published {PublishedProjects} for processing.",
            response.NewCount, response.Skipped, response.Published);

        return new JobExecutionResult(
            Success: true,
            Message: $"Discovered {response.Discovered}, matched {response.Matched}, published {response.Published}.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
