using CodeGraph.Data;
using CodeGraph.Models.Work;
using Microsoft.Extensions.Options;

namespace CodeGraph.Jobs.Jobs;

/// <summary>Deletes work sessions that ended or went quiet longer ago than the retention window (sc-27882).</summary>
public class PruneWorkSessionsJob(
    IWorkSessionPruner pruner,
    IOptions<WorkSessionOptions> options) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var settings = options.Value;
        // Never shorter than the direct-message inheritance window, whatever the configuration says.
        var minimumDays = (int)Math.Ceiling(WorkMessageLimits.DirectMessageRetention.TotalDays);
        var days = Math.Max(settings.RetentionDays > 0 ? settings.RetentionDays : new WorkSessionOptions().RetentionDays, minimumDays);
        var result = await pruner.PruneSessionsAsync(startedAtUtc.AddDays(-days), settings.PruneBatchSize, ct);

        return new JobExecutionResult(
            Success: true,
            Message: $"Work session pruning deleted {result.SessionsDeleted} sessions and {result.ClaimRowsDeleted} closed claim rows older than {days} days.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
