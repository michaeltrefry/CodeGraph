using CodeGraph.Models.Work;
using CodeGraph.Services.Work.Imports;

namespace CodeGraph.Jobs.Jobs;

/// <summary>
/// Arguments of the WorkImport job (sc-27876). Defaults: the Shortcut delta sync (updated since the last
/// completed import), so a plain schedule keeps CodeGraph in step with Shortcut until cutover.
/// </summary>
public sealed class WorkImportJobRequest
{
    /// <summary>shortcut (default) or kanban.</summary>
    public string? Source { get; set; }

    /// <summary>dry_run, import or delta (default).</summary>
    public string? Mode { get; set; }

    public DateTime? UpdatedSince { get; set; }
}

/// <summary>Runs one recorded import (sc-27876): a dry run, a full import or a delta sync. Sources are only read.</summary>
// The runner is registered with the MariaDB provider only; without it the job fails with a plain message.
public class WorkImportJob(IWorkImportRunner? runner = null) : IJobCommand<WorkImportJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(WorkImportJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        if (runner is null)
            throw new WorkStoreUnavailableException();
        var source = string.IsNullOrWhiteSpace(request.Source) ? ImportSources.Shortcut : request.Source.Trim().ToLowerInvariant();
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "delta" : request.Mode.Trim();
        var run = await runner.RunAsync(source, mode, request.UpdatedSince, "job:WorkImport", ct);
        var written = run.Report?.Written;
        var message = run.Status == ImportRunStatuses.Completed
            ? $"{source} {mode} run {run.Id} completed: {run.Report?.Stories.Mapped ?? 0} items mapped" +
              (written is null || run.Mode == ImportMode.DryRun
                  ? "."
                  : $"; {written.ItemsCreated} created, {written.ItemsUpdated} updated, {written.ItemsUnchanged} unchanged, {written.Failures} failures.")
            : $"{source} {mode} run {run.Id} failed: {run.Error}";
        return new JobExecutionResult(run.Status == ImportRunStatuses.Completed, message, startedAtUtc, DateTime.UtcNow);
    }
}
