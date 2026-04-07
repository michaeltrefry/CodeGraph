using CodeGraph.Jobs.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Jobs.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController(IJobRunner runner) : ControllerBase
{
    /// <summary>
    /// Discover repositories from the configured source provider and publish downstream processing messages.
    /// </summary>
    [HttpPost(nameof(DiscoverRepositoriesJob))]
    public StartJobResult DiscoverRepositories([FromBody] StartJob? request)
    {
        return runner.RunJob<DiscoverRepositoriesJob>(
            new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
    }

    /// <summary>
    /// Publish ProcessRepository messages for a list of repositories.
    /// Args: repos (required), shouldIndex, shouldAnalyze, skipIfUpToDate
    /// </summary>
    [HttpPost(nameof(ProcessRepositoriesJob))]
    public StartJobResult ProcessRepositories([FromBody] StartJob request)
    {
        return runner.RunJob<ProcessRepositoriesJob>(
            new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
    }

    /// <summary>
    /// Poll the Anthropic Batches API for completed results and store them.
    /// Args: repo (optional)
    /// </summary>
    [HttpPost(nameof(ProcessBatchResultsJob))]
    public StartJobResult ProcessBatchResults([FromBody] StartJob request)
    {
        return runner.RunJob<ProcessBatchResultsJob>(
            new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
    }
}
