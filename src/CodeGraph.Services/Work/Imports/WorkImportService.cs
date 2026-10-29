using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work.Imports;

/// <summary>Opens the read-only sources of an import. The default reads the REST APIs with tokens from MCP Hub credentials.</summary>
public interface IWorkImportSourceFactory
{
    Task<IShortcutSource> ShortcutAsync(CancellationToken ct);
    Task<IKanbanSource> KanbanAsync(CancellationToken ct);
}

public sealed class RestWorkImportSourceFactory(IHttpClientFactory http, IMcpHubStore hub, IOptions<WorkImportOptions> options) : IWorkImportSourceFactory
{
    public async Task<IShortcutSource> ShortcutAsync(CancellationToken ct) => await ShortcutRestSource.CreateAsync(http, hub, options, ct);
    public async Task<IKanbanSource> KanbanAsync(CancellationToken ct) => await KanbanRestSource.CreateAsync(http, hub, options, ct);
}

/// <summary>What the WorkImport job runs: one recorded import run, to completion.</summary>
public interface IWorkImportRunner
{
    Task<ImportRunView> RunAsync(string source, string? modeName, DateTime? updatedSince, string user, CancellationToken ct);
}

/// <summary>
/// Runs imports as recorded runs (sc-27876): the REST trigger, the job and the tests all go through
/// here. One run per source at a time; each run stores its report. <c>delta</c> is an import with
/// updated_since defaulting to the start of the last completed import.
/// </summary>
public sealed partial class WorkImportService(
    IWorkImportStore imports,
    ShortcutImporter shortcut,
    KanbanImporter kanban,
    IWorkImportSourceFactory sources,
    ILogger<WorkImportService>? logger = null) : IWorkImportRunner
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private readonly ILogger log = logger ?? (ILogger)NullLogger.Instance;

    [GeneratedRegex("^[A-Z][A-Z0-9]{1,9}$")]
    private static partial Regex ProjectKey();

    public static (ImportMode Mode, bool Delta) ParseMode(string? mode) => (mode ?? "dry_run").Trim().ToLowerInvariant() switch
    {
        "dry_run" or "dryrun" or "dry-run" => (ImportMode.DryRun, false),
        "import" => (ImportMode.Import, false),
        "delta" => (ImportMode.Import, true),
        var other => throw WorkException.Invalid($"mode must be dry_run, import or delta; not '{other}'.")
    };

    private static string RequireSource(string source) => source switch
    {
        ImportSources.Shortcut or ImportSources.Kanban => source,
        _ => throw WorkException.NotFound($"There is no import source '{source}' (shortcut, kanban).")
    };

    /// <summary>The saved settings, or (none saved yet) the proposal of a dry run.</summary>
    public async Task<ImportSettings> GetSettingsAsync(string source, CancellationToken ct)
    {
        RequireSource(source);
        if (await imports.GetImportSettingsAsync(source, ct) is { } stored)
            return stored;
        return source == ImportSources.Kanban ? KanbanImporter.Propose() : await shortcut.ProposeAsync(await sources.ShortcutAsync(ct), ct);
    }

    public async Task<ImportSettings> SaveSettingsAsync(string source, ImportSettings settings, bool approve, string user, CancellationToken ct)
    {
        RequireSource(source);
        var keys = settings.Teams.Select(team => team.ProjectKey).Append(settings.HoldingProjectKey).ToList();
        var bad = keys.Where(key => !ProjectKey().IsMatch(key)).Distinct().ToList();
        if (bad.Count > 0)
            throw WorkException.Invalid($"Project keys are 2–10 upper-case letters and digits, starting with a letter; not: {string.Join(", ", bad)}. Nothing was saved.");
        var duplicates = settings.Teams.GroupBy(team => team.TeamKey).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicates.Count > 0)
            throw WorkException.Invalid($"Each team appears once in the table; repeated: {string.Join(", ", duplicates)}. Nothing was saved.");
        if (settings.Members?.FirstOrDefault(member => member.Kind is not (WorkActor.UserKind or WorkActor.AgentKind)) is { } wrong)
            throw WorkException.Invalid($"A member's kind is user or agent; {wrong.MemberId} has '{wrong.Kind}'. Nothing was saved.");
        return await imports.SaveImportSettingsAsync(source, settings, approve ? user : null, ct);
    }

    /// <summary>Records a run and returns its id; <see cref="ExecuteAsync"/> then does the work.</summary>
    public async Task<(Guid RunId, ImportMode Mode, DateTime? UpdatedSince)> StartAsync(string source, string? modeName, DateTime? updatedSince, string user, CancellationToken ct)
    {
        RequireSource(source);
        var (mode, delta) = ParseMode(modeName);
        if (delta && source == ImportSources.Kanban)
            throw WorkException.Invalid("The KanbanBoard import has no delta mode: run import again (it is idempotent).");
        if (delta && updatedSince is null)
            updatedSince = await imports.LastCompletedImportStartAsync(source, ct)
                ?? throw WorkException.Invalid("A delta sync needs updated_since, or a completed import to continue from. Run a full import first.");
        if (mode == ImportMode.Import && (await imports.GetImportSettingsAsync(source, ct))?.ApprovedAt is null)
            throw WorkException.Invalid($"The {source} import settings are not approved yet: review the dry-run report, then approve them. Nothing was imported.");
        var runId = await imports.StartImportRunAsync(source, mode, updatedSince, user, StaleAfter, ct);
        return (runId, mode, updatedSince);
    }

    public async Task<ImportRunView> ExecuteAsync(Guid runId, string source, ImportMode mode, DateTime? updatedSince, CancellationToken ct)
    {
        var report = new ImportReport { Source = source, Mode = mode, UpdatedSince = updatedSince };
        try
        {
            report = source == ImportSources.Shortcut
                ? await shortcut.RunAsync(await sources.ShortcutAsync(ct), mode, updatedSince, progress => imports.HeartbeatImportRunAsync(runId, progress, ct), ct)
                : await kanban.RunAsync(await sources.KanbanAsync(ct), mode, ct);
            await imports.CompleteImportRunAsync(runId, ImportRunStatuses.Completed, report, null, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.LogError(exception, "Import run {RunId} of {Source} failed", runId, source);
            await imports.CompleteImportRunAsync(runId, ImportRunStatuses.Failed, report, exception.Message, CancellationToken.None);
        }

        return (await imports.GetImportRunAsync(runId, CancellationToken.None))!;
    }

    public async Task<ImportRunView> RunAsync(string source, string? modeName, DateTime? updatedSince, string user, CancellationToken ct)
    {
        var (runId, mode, since) = await StartAsync(source, modeName, updatedSince, user, ct);
        return await ExecuteAsync(runId, source, mode, since, ct);
    }
}
