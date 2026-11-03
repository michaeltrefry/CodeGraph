using CodeGraph.Data;
using CodeGraph.Models.Memory;

namespace CodeGraph.Jobs.Jobs;

/// <summary>
/// Reconciles memory project scopes with what changed since the last successful run (CG-172), never
/// every project's full scan:
/// <list type="bullet">
/// <item>Every project with repositories still pending (its create or repository change recorded
/// them; their backfill failed, was dropped by a full queue or lost to a restart) gets the full
/// backfill of those repositories, and they leave the pending set when it succeeds.</item>
/// <item>The memory written or rewritten since the last run (above the stored watermark) is
/// backfilled for every project, restricted to those rows, in spans of at most <see cref="DefaultRangeSpan"/>
/// marks per table; the watermark advances after each span that succeeds, so a failure keeps the
/// progress before it. The first run records the current watermark without a scan: migration 114, the
/// create and add backfills and migration 116's one-time pending rows cover the memory below it.</item>
/// </list>
/// With nothing pending and no new memory, a run executes no scan. One project failing never stops the
/// others; the run fails, naming them, and what failed stays pending for the next run.
/// </summary>
public sealed class ReconcileMemoryProjectScopesJob(
    IWorkStore workStore,
    IMemoryScopeReconciliationStore reconciliation,
    IMemoryProjectScopeBackfill backfill,
    ILogger<ReconcileMemoryProjectScopesJob> logger,
    long rangeSpan = ReconcileMemoryProjectScopesJob.DefaultRangeSpan) : IJobCommand<EmptyJobRequest>
{
    /// <summary>The most entity marks, claim marks and evidence ids one range backfill call covers.</summary>
    public const long DefaultRangeSpan = 5_000;

    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var since = await reconciliation.GetWatermarkAsync(ct);
        // The watermark is read before the membership: memory up to it was written before any repository
        // added after the listing below, and that repository's own pending backfill scans it in full.
        var current = await backfill.GetWatermarkAsync(null, ct);
        // A stored watermark the memory database is behind (restored, truncated, or memory moved to
        // another database) would skip everything up to it: start again as a first run.
        var reset = since is not null && since.IsAheadOf(current);
        if (reset)
        {
            logger.LogWarning("The memory scope watermark {Watermark} is ahead of memory ({Current}); every project is backfilled again.", since, current);
            since = null;
        }
        var upTo = since is null ? current : await backfill.GetWatermarkAsync(since, ct);
        var projects = await workStore.ListProjectRepositoriesAsync(ct);
        if (reset)
        {
            foreach (var project in projects)
                await reconciliation.AddPendingAsync(project.ProjectKey, project.Repositories, ct);
        }

        var members = projects.ToDictionary(project => project.ProjectKey, project => project.Repositories, StringComparer.Ordinal);
        var failures = new List<string>();
        var pending = await reconciliation.ListPendingAsync(ct);
        var repaired = 0;
        foreach (var row in pending)
        {
            // Only repositories the listing above shows as members are backfilled. One it does not show
            // (removed since, or added after the listing) is left to the store, which drops it only if the
            // project no longer has it.
            var repositories = members.TryGetValue(row.ProjectKey, out var listed)
                ? row.Repositories.Where(name => listed.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList()
                : [];
            try
            {
                if (repositories.Count > 0)
                    await backfill.BackfillAsync(row.ProjectKey, repositories, ct);
                await reconciliation.CompletePendingAsync(row.ProjectKey, repositories, ct);
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
            newMemory = reset
                ? "reset the memory watermark, which was ahead of memory, without a scan"
                : "recorded the memory watermark (first run, no scan)";
        }
        else if (upTo == since)
        {
            newMemory = "no new memory";
        }
        else
        {
            var from = since;
            var spans = 0;
            try
            {
                while (from != upTo)
                {
                    var to = new MemoryScopeWatermark(
                        Math.Min(upTo.EntitySeq, from.EntitySeq + rangeSpan),
                        Math.Min(upTo.ClaimSeq, from.ClaimSeq + rangeSpan),
                        Math.Min(upTo.EvidenceId, from.EvidenceId + rangeSpan));
                    await backfill.BackfillRangeAsync(projects, from, to, ct);
                    await reconciliation.SetWatermarkAsync(to, ct);
                    from = to;
                    spans++;
                }
                newMemory = $"scoped new memory from {since} to {upTo} in {spans} spans for {projects.Count} projects";
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "New memory could not be scoped to projects; the watermark stays at {Watermark}.", from);
                failures.Add($"new memory above {from} ({exception.Message})");
                newMemory = $"scoped new memory up to {from} in {spans} spans, not up to {upTo}";
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
