using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Services;
using TC.Common.TcServiceStack.Queue.Abstractions;
using TC.JobUtilities;

namespace TC.CodeGraphJobs.Jobs;

/// <summary>
/// Polls the Anthropic Batches API for completed batches and stores results.
/// Schedule on a regular cadence (e.g. every 30 minutes).
///
/// Args:
///   repo — optional; scopes polling to a single repo
/// </summary>
public class ProcessBatchResultsJob(
    ILogger<ProcessBatchResultsJob> logger,
    ITcServiceBus serviceBus,
    IBatchAnalysisService batchService,
    Guid instanceKey)
    : Job(logger, serviceBus, instanceKey)
{
    protected override async Task ExecuteAsync(StartJob startJob)
    {
        string? repo = null;
        startJob.Args?.TryGetValue("repo", out repo);
        await batchService.ProcessCompletedBatchesAsync(repo);
    }
}
