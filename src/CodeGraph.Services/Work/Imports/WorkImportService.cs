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

public sealed record StartedRun(Guid RunId, string Source, ImportMode Mode, DateTime? UpdatedSince, IReadOnlyList<string> Retry);

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

    /// <summary>How far before the last completed import's start a delta sync reads from (search-index lag, clock skew).</summary>
    public static readonly TimeSpan DeltaOverlap = TimeSpan.FromMinutes(30);
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
        if (source == ImportSources.Kanban && settings.KanbanEpicId is { } epic && epic != KanbanImporter.CodeReviewEpicId)
            throw WorkException.Invalid($"Only the KanbanBoard epic SceneWorks Code Review 2026-10-27 ({KanbanImporter.CodeReviewEpicId}) is imported (D23); not {epic}. Nothing was saved.");
        if (settings.Teams.Any(team => team.Repositories is { Count: > 0 }))
            settings = settings with { Teams = await CheckRepositoriesAsync(settings.Teams, ct) };
        return await imports.SaveImportSettingsAsync(source, settings, approve ? user : null, ct);
    }

    /// <summary>
    /// The table's repositories (sc-27928): each one CodeGraph indexes (spelled as CodeGraph does), given to one
    /// project only, and owned by no other project: a repository is owned by at most one project (E2), and the
    /// import never takes one from its owner.
    /// </summary>
    private async Task<List<ImportTeamMapping>> CheckRepositoriesAsync(IReadOnlyList<ImportTeamMapping> teams, CancellationToken ct)
    {
        if (teams.FirstOrDefault(team => team.Repositories?.Any(string.IsNullOrWhiteSpace) == true) is { } blank)
            throw WorkException.Invalid($"The repositories of team {blank.TeamName ?? blank.TeamKey} hold an empty name: each is a repository CodeGraph indexes. Nothing was saved.");
        var index = new RepositoryIndex(await imports.ListIndexedRepositoriesAsync(ct));
        var unknown = teams.SelectMany(team => team.Repositories ?? []).Where(name => index.Named(name) is null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
            var known = index.All.Select(repository => repository.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            throw WorkException.Invalid($"CodeGraph indexes no repository named {string.Join(", ", unknown.Select(name => $"'{name}'"))}. " +
                $"Indexed repositories: {(known.Count == 0 ? "none" : string.Join(", ", known.Take(100)) + (known.Count > 100 ? $" and {known.Count - 100} more" : ""))}. Nothing was saved.");
        }

        var result = teams.Select(team => team.Repositories is null ? team
            : team with { Repositories = team.Repositories.Select(name => index.Named(name)!.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }).ToList();
        var given = result.SelectMany(team => (team.Repositories ?? []).Select(repository => (Repository: repository, team.ProjectKey))).Distinct().ToList();
        var split = given.GroupBy(pair => pair.Repository, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToList();
        if (split.Count > 0)
            throw WorkException.Invalid("A repository is owned by one project, but the table gives " +
                string.Join("; ", split.Select(group => $"{group.Key} to {string.Join(" and ", group.Select(pair => pair.ProjectKey))}")) + ". Nothing was saved.");
        var taken = given.Where(pair => index.Named(pair.Repository)!.OwnedBy is { } owner && owner != pair.ProjectKey).ToList();
        if (taken.Count > 0)
            throw new WorkException(WorkErrorCodes.Conflict, "A repository is owned by one project: " +
                string.Join("; ", taken.Select(pair => $"{pair.Repository} is owned by {index.Named(pair.Repository)!.OwnedBy}, so {pair.ProjectKey} cannot own it")) +
                ". Take it out of the table, or change the owning project first. Nothing was saved.");
        return result;
    }

    /// <summary>
    /// Records a run and returns its id; <see cref="ExecuteAsync"/> then does the work. A delta continues
    /// from the start of the last completed import (or <paramref name="updatedSince"/>) and reads again
    /// what that run failed on.
    /// </summary>
    public async Task<StartedRun> StartAsync(string source, string? modeName, DateTime? updatedSince, string user, CancellationToken ct)
    {
        RequireSource(source);
        var (mode, delta) = ParseMode(modeName);
        if (delta && source == ImportSources.Kanban)
            throw WorkException.Invalid("The KanbanBoard import has no delta mode: run import again (it is idempotent).");
        IReadOnlyList<string> retry = [];
        if (delta)
        {
            var last = await imports.LastCompletedImportAsync(source, ct);
            // From the last run's start less an overlap: Shortcut's search index lags its writes and its clock is
            // not CodeGraph's, so a story updated just before that start may not have been listed by it (sc-27929).
            // Reading it again is harmless: an unchanged story is skipped by its content hash.
            updatedSince ??= last?.StartedAt - DeltaOverlap
                ?? throw WorkException.Invalid("A delta sync needs updated_since, or a completed import to continue from. Run a full import first.");
            retry = last?.Report?.FailedIds ?? [];
        }

        if (mode == ImportMode.Import && (await imports.GetImportSettingsAsync(source, ct))?.ApprovedAt is null)
            throw WorkException.Invalid($"The {source} import settings are not approved yet: review the dry-run report, then approve them. Nothing was imported.");
        var runId = await imports.StartImportRunAsync(source, mode, updatedSince, user, StaleAfter, ct);
        return new StartedRun(runId, source, mode, updatedSince, retry);
    }

    /// <summary>How often a run's heartbeat moves while it works, in every phase.</summary>
    public static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<ImportRunView> ExecuteAsync(StartedRun run, CancellationToken ct)
    {
        // The importer fills this report as it goes: a failed run is completed with the progress it made.
        var report = new ImportReport { Source = run.Source, Mode = run.Mode, UpdatedSince = run.UpdatedSince };
        using var beating = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = BeatAsync(run.RunId, beating.Token);
        try
        {
            if (run.Source == ImportSources.Shortcut)
                await shortcut.RunAsync(await sources.ShortcutAsync(ct), run.Mode, run.UpdatedSince, report, run.Retry,
                    progress => imports.HeartbeatImportRunAsync(run.RunId, progress, ct), ct);
            else
                await kanban.RunAsync(await sources.KanbanAsync(ct), run.Mode, report, ct);
            await StopAsync(beating, heartbeat);
            if (!await imports.CompleteImportRunAsync(run.RunId, ImportRunStatuses.Completed, report, null, CancellationToken.None))
                log.LogWarning("Import run {RunId} was no longer running when it finished; its result was not recorded", run.RunId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.LogError(exception, "Import run {RunId} of {Source} failed", run.RunId, run.Source);
            await StopAsync(beating, heartbeat);
            await imports.CompleteImportRunAsync(run.RunId, ImportRunStatuses.Failed, report, exception.Message, CancellationToken.None);
        }

        return (await imports.GetImportRunAsync(run.RunId, CancellationToken.None))!;
    }

    private async Task BeatAsync(Guid runId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    // The heartbeat only: the progress report is saved by the importer itself.
                    await imports.HeartbeatImportRunAsync(runId, null, ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.LogWarning(exception, "Heartbeat of import run {RunId} failed", runId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task StopAsync(CancellationTokenSource beating, Task heartbeat)
    {
        await beating.CancelAsync();
        await heartbeat;
    }

    public async Task<ImportRunView> RunAsync(string source, string? modeName, DateTime? updatedSince, string user, CancellationToken ct)
    {
        var run = await StartAsync(source, modeName, updatedSince, user, ct);
        return await ExecuteAsync(run, ct);
    }
}
