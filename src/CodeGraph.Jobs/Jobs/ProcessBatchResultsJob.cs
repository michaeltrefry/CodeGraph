using CodeGraph.Services.Analyzers;

namespace CodeGraph.Jobs.Jobs;

/// <summary>
/// Polls the active analysis provider for completed batches and stores results.
/// Schedule on a regular cadence (e.g. every 30 minutes).
///
/// Args:
///   repo — optional; scopes polling to a single repo
/// </summary>
public class ProcessBatchResultsJob(
    IBatchAnalysisService batchService) : IJob
{
    public async Task ExecuteAsync(StartJob startJob, CancellationToken ct = default)
    {
        string? repo = null;
        startJob.Args?.TryGetValue("repo", out repo);
        await batchService.ProcessCompletedBatchesAsync(repo, ct);
    }
}
