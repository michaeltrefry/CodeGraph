using Microsoft.AspNetCore.Mvc;
using TC.CodeGraphJobs.Jobs;
using TC.JobUtilities;
using TC.JobUtilities.Controllers;

namespace TC.CodeGraphJobs.Controllers;

public class JobsController(ILogger<JobsController> logger, IJobRunner runner)
    : JobExecutionController(logger, runner)
{
    /// <summary>
    /// Discover all GitLab repositories and publish downstream processing messages.
    /// </summary>
    [HttpPost(nameof(DiscoverRepositoriesJob))]
    public StartJobResult DiscoverRepositoriesJob([FromBody] StartJob? request)
    {
        return JobRunner.RunJob<DiscoverRepositoriesJob>(
            new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
    }

    /// <summary>
    /// Publish ProcessRepository messages for a list of repositories.
    /// Args: repos (required), shouldIndex, shouldAnalyze, skipIfUpToDate
    /// </summary>
    [HttpPost(nameof(ProcessRepositoriesJob))]
    public StartJobResult ProcessRepositoriesJob([FromBody] StartJob request)
    {
        return JobRunner.RunJob<ProcessRepositoriesJob>(
            new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
    }

    /// <summary>
    /// Poll the Anthropic Batches API for completed results and store them.
    /// Args: repo (optional)
    /// </summary>
    [HttpPost(nameof(ProcessBatchResultsJob))]
    public StartJobResult ProcessBatchResultsJob([FromBody] StartJob request)
    {
        return JobRunner.RunJob<ProcessBatchResultsJob>(
            new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
    }
}
