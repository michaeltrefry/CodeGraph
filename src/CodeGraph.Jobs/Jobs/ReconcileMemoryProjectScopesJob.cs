using CodeGraph.Data;

namespace CodeGraph.Jobs.Jobs;

/// <summary>
/// Reconciles memory project scopes with what changed since the last successful run (CG-172), never
/// every project's full scan:
/// <list type="bullet">
/// <item>Every project with repositories still pending (its create or repository change recorded
/// them; their backfill failed, was dropped by a full queue or lost to a restart) gets the full
/// backfill of those repositories, and they leave the pending set when it succeeds.</item>
/// <item>The memory written since the last run (above the stored watermark) is backfilled for every
/// project, restricted to those rows, and the watermark advances only when that succeeds. The first
/// run records the current watermark without a scan: migration 114, the create and add backfills and
/// migration 116's one-time pending rows cover the memory below it.</item>
/// </list>
/// With nothing pending and no new memory, a run executes no scan. One project failing never stops the
/// others; the run fails, naming them, and what failed stays pending for the next run.
/// </summary>
public sealed class ReconcileMemoryProjectScopesJob(
    IWorkStore workStore,
    IMemoryScopeReconciliationStore reconciliation,
    IMemoryProjectScopeBackfill backfill,
    ILogger<ReconcileMemoryProjectScopesJob> logger) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var since = await reconciliation.GetWatermarkAsync(ct);
        // The watermark is read before the membership: memory up to it was written before any repository
        // added after the listing below, and that repository's own pending backfill scans it in full.
        var upTo = await backfill.GetWatermarkAsync(since, ct);
        var projects = await workStore.ListProjectRepositoriesAsync(ct);
        var members = projects.ToDictionary(project => project.ProjectKey, project => project.Repositories, StringComparer.Ordinal);
        var failures = new List<string>();

        var pending = await reconciliation.ListPendingAsync(ct);
        var repaired = 0;
        foreach (var row in pending)
        {
            // A repository the project no longer has, or a project since deleted, has nothing left to scope.
            var repositories = members.TryGetValue(row.ProjectKey, out var current)
                ? row.Repositories.Where(name => current.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList()
                : [];
            try
            {
                if (repositories.Count > 0)
                    await backfill.BackfillAsync(row.ProjectKey, repositories, ct);
                await reconciliation.CompletePendingAsync(row.ProjectKey, row.Repositories, ct);
                repaired++;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Memory naming the repositories of project {Project} could not be scoped to it.", row.ProjectKey);
                failures.Add($"{row.ProjectKey} ({exception.Message})");
                await RecordFailureAsync(row.ProjectKey, exception, ct);
            }
        }

        string newMemory;
        if (since is null)
        {
            await reconciliation.SetWatermarkAsync(upTo, ct);
            newMemory = "recorded the memory watermark (first run, no scan)";
        }
        else if (upTo == since)
        {
            newMemory = "no new memory";
        }
        else
        {
            try
            {
                await backfill.BackfillRangeAsync(projects, since, upTo, ct);
                await reconciliation.SetWatermarkAsync(upTo, ct);
                newMemory = $"scoped new memory (entities {since.EntityId}-{upTo.EntityId}, claims {since.ClaimId}-{upTo.ClaimId}, evidence {since.EvidenceId}-{upTo.EvidenceId}) for {projects.Count} projects";
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "New memory could not be scoped to projects; the watermark stays at {Watermark}.", since);
                failures.Add($"new memory ({exception.Message})");
                newMemory = "new memory not scoped";
            }
        }

        var summary = $"{repaired} of {pending.Count} pending projects backfilled; {newMemory}.";
        return failures.Count == 0
            ? new JobExecutionResult(true, $"Memory scope reconciliation: {summary}", startedAtUtc, DateTime.UtcNow)
            : new JobExecutionResult(false,
                $"Memory scope reconciliation failed for {string.Join("; ", failures)}. {summary}",
                startedAtUtc, DateTime.UtcNow);
    }

    private async Task RecordFailureAsync(string projectKey, Exception failure, CancellationToken ct)
    {
        try
        {
            await reconciliation.FailPendingAsync(projectKey, failure.Message, ct);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            // The repositories stay pending either way; only the error is not kept.
            logger.LogWarning(exception, "The failed memory scope backfill of project {Project} could not be recorded.", projectKey);
        }
    }
}
