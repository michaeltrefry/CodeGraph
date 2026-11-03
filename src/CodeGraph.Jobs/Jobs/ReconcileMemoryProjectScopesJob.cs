using CodeGraph.Data;

namespace CodeGraph.Jobs.Jobs;

/// <summary>
/// Re-runs every project's memory scope backfill (CG-167). A project's create or repository change
/// runs it once, best effort; one that failed (the memory host was down, say) is retried here, so a
/// project never permanently misses the scopes of the memory naming its repositories. The backfill
/// is idempotent: a project already scoped changes nothing. One project failing never stops the
/// others; the run fails, naming them, until a later run reconciles every project.
/// </summary>
public sealed class ReconcileMemoryProjectScopesJob(
    IWorkStore workStore,
    IMemoryProjectScopeBackfill backfill,
    ILogger<ReconcileMemoryProjectScopesJob> logger) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var projects = await workStore.ListProjectRepositoriesAsync(ct);
        var failures = new List<string>();
        foreach (var project in projects)
        {
            try
            {
                await backfill.BackfillAsync(project.ProjectKey, project.Repositories, ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Memory naming the repositories of project {Project} could not be scoped to it.", project.ProjectKey);
                failures.Add($"{project.ProjectKey} ({exception.Message})");
            }
        }

        var reconciled = projects.Count - failures.Count;
        return failures.Count == 0
            ? new JobExecutionResult(true,
                $"Memory scope reconciliation backfilled {reconciled} projects with member repositories.",
                startedAtUtc, DateTime.UtcNow)
            : new JobExecutionResult(false,
                $"Memory scope reconciliation failed for {failures.Count} of {projects.Count} projects: {string.Join("; ", failures)}. {reconciled} projects were backfilled.",
                startedAtUtc, DateTime.UtcNow);
    }
}
