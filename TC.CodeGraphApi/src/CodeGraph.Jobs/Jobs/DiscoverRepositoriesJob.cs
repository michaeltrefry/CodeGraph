using CodeGraph.Services;

namespace CodeGraph.Jobs.Jobs;

/// <summary>
/// Discovers all GitLab repositories and triggers processing via the admin service.
/// </summary>
public class DiscoverRepositoriesJob(
    IAdminService adminService,
    ILogger<DiscoverRepositoriesJob> logger) : IJob
{
    public async Task ExecuteAsync(StartJob startJob, CancellationToken ct = default)
    {
        var response = await adminService.DiscoverAsync(new CodeGraph.Models.Requests.DiscoverRequest
        {
            IncludeAllSource = true,
            Limit = null,
            ShouldIndex = true,
            ShouldAnalyze = true,
            SkipIfUpToDate = true
        });

        logger.LogInformation(
            "Discovered {NewProjects} new projects. Skipped {SkippedProjects} existing. Publishing {PublishedProjects} for processing.",
            response.NewCount, response.Skipped, response.Published);
    }
}
