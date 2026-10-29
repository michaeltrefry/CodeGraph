using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work.Imports;

/// <summary>
/// The Shortcut migration (design §10.1): a dry run that reads everything and reports the mapping, and
/// an import (full, or a delta sync by updated_since) that writes it through the work store, keyed by
/// Shortcut id so re-runs update instead of duplicating. Shortcut is only read.
/// </summary>
public sealed class ShortcutImporter(
    IWorkStore work,
    IWorkImportStore imports,
    IWorkAttachmentStorage files,
    IOptions<WorkImportOptions> options,
    ILogger<ShortcutImporter>? logger = null)
{
    public const string DefaultHoldingProject = "SCIMPORT";
    public const string NoTeam = "none";
    private const int ListedLimit = 500;

    private readonly ILogger log = logger ?? (ILogger)NullLogger.Instance;
    private static readonly WorkActor Actor = ImportSources.Actor(ImportSources.Shortcut);

    private sealed class ProjectTarget(string key)
    {
        public string Key { get; } = key;
        public Guid? Id { get; set; }
        public IReadOnlyList<string> Owned { get; set; } = [];
    }

    /// <summary>
    /// The proposed team → project table: a team whose name is a CodeGraph project's name maps to it,
    /// otherwise to a key made from its name; work without a team goes to the holding project.
    /// </summary>
    public async Task<ImportSettings> ProposeAsync(IShortcutSource source, CancellationToken ct)
    {
        var teams = await source.ListTeamsAsync(ct);
        var members = await source.ListMembersAsync(ct);
        var projects = await ListProjectsAsync(ct);
        var mappings = teams.OrderBy(team => team.Name, StringComparer.OrdinalIgnoreCase).Select(team =>
        {
            var existing = projects.FirstOrDefault(project => project.Name.Equals(team.Name, StringComparison.OrdinalIgnoreCase));
            return new ImportTeamMapping(team.Id, existing?.Key ?? ShortcutMapper.ProposedKey(team.Name), team.Name, existing?.Name ?? team.Name);
        }).ToList();
        mappings.Add(new ImportTeamMapping(NoTeam, DefaultHoldingProject, "(no team)", "Shortcut import (unmapped)"));
        return new ImportSettings(DefaultHoldingProject, mappings, members.Select(ShortcutMapper.DefaultMember).ToList());
    }

    private async Task<List<ProjectView>> ListProjectsAsync(CancellationToken ct)
    {
        var result = new List<ProjectView>();
        string? after = null;
        while (true)
        {
            var page = await work.FindAsync(new FindWorkSpec(WorkEntityKind.Project, null, null, null, null, null, null, null, null, null, [], WorkDetail.Full, 200, after), ct);
            var projects = page.Items.OfType<ProjectView>().ToList();
            result.AddRange(projects);
            if (page.NextCursor is null || projects.Count == 0)
                return result;
            after = projects[^1].Key;
        }
    }

    /// <summary>
    /// Whether a failure belongs to one source object (recorded in the report, the run goes on): a
    /// refused write, an HTTP failure or timeout (not the run's own cancellation), a database error the
    /// store did not retry away, an unreadable payload or file.
    /// </summary>
    public static bool IsItemFailure(Exception exception, CancellationToken ct) =>
        exception is WorkException or HttpRequestException or IOException or DbException or JsonException
        || (exception is OperationCanceledException && !ct.IsCancellationRequested);

    /// <param name="report">Filled as the run goes, so a failed run keeps the progress it made.</param>
    /// <param name="retry">Source ids a previous run failed on (sc-N, epic-N): read again whatever updated_since says.</param>
    /// <param name="progress">Called every 100 stories to save the progress.</param>
    public async Task<ImportReport> RunAsync(IShortcutSource source, ImportMode mode, DateTime? updatedSince, ImportReport report, IReadOnlyCollection<string>? retry,
        Func<ImportReport, Task>? progress, CancellationToken ct)
    {
        var retried = (retry ?? []).ToHashSet(StringComparer.Ordinal);
        var stored = await imports.GetImportSettingsAsync(ImportSources.Shortcut, ct);
        var approved = stored?.ApprovedAt is not null;
        if (mode == ImportMode.Import && !approved)
            throw WorkException.Invalid("The team → project table has not been approved. Review the dry-run report, then approve the table (PUT /api/work/imports/shortcut/settings with approve: true) before importing. Nothing was imported.");
        // A dry run before approval reports the saved (unapproved) table, or the proposal.
        var settings = stored ?? await ProposeAsync(source, ct);
        report.Source = ImportSources.Shortcut;
        report.Mode = mode;
        report.UpdatedSince = updatedSince;
        report.SettingsApproved = approved;
        report.HoldingProject = settings.HoldingProjectKey;

        var teams = (await source.ListTeamsAsync(ct)).ToDictionary(team => team.Id, StringComparer.Ordinal);
        var memberList = await source.ListMembersAsync(ct);
        var members = memberList.Select(ShortcutMapper.DefaultMember).ToDictionary(member => member.MemberId, StringComparer.Ordinal);
        foreach (var mapped in settings.Members ?? [])
            members[mapped.MemberId] = mapped;
        var states = (await source.ListStatesAsync(ct)).GroupBy(state => state.Id).ToDictionary(group => group.Key, group => group.First());

        // ---- The team → project table ----------------------------------------------------------
        var table = settings.Teams.ToDictionary(row => row.TeamKey, StringComparer.Ordinal);
        var targets = new Dictionary<string, ProjectTarget>(StringComparer.Ordinal);
        var teamRows = new Dictionary<string, ImportTeamRow>(StringComparer.Ordinal);
        ImportTeamRow TeamRow(string? teamId)
        {
            var key = teamId ?? NoTeam;
            if (teamRows.TryGetValue(key, out var row))
                return row;
            var mapped = table.GetValueOrDefault(key);
            row = new ImportTeamRow
            {
                TeamKey = key,
                TeamName = teamId is null ? "(no team)" : teams.GetValueOrDefault(teamId)?.Name ?? mapped?.TeamName ?? teamId,
                ProjectKey = mapped?.ProjectKey ?? settings.HoldingProjectKey,
                Mapping = mapped is null ? "holding" : approved ? "approved" : "proposed"
            };
            teamRows[key] = row;
            return row;
        }

        foreach (var team in teams.Values)
            TeamRow(team.Id);
        TeamRow(null);
        foreach (var key in teamRows.Values.Select(row => row.ProjectKey).Append(settings.HoldingProjectKey).Distinct())
            targets[key] = new ProjectTarget(key);

        foreach (var target in targets.Values)
        {
            if (await work.ResolveAsync(target.Key, ct) is { Kind: WorkEntityKind.Project } found && await work.GetProjectAsync(found.Id, ct) is { } project)
            {
                target.Id = project.Id;
                target.Owned = project.Repositories.Where(member => member.Role == RepositoryRole.Owned).Select(member => member.Repository).ToList();
            }
        }

        // A project of the table that does not exist yet is created when the first work for it is written.
        async Task<Guid> EnsureProjectAsync(ProjectTarget target)
        {
            if (target.Id is { } existing)
                return existing;
            var name = settings.Teams.FirstOrDefault(row => row.ProjectKey == target.Key)?.ProjectName ?? target.Key;
            var created = await work.CreateProjectAsync(new NewProjectSpec(target.Key, name, "Created by the Shortcut import (sc-27876).", [], []), Actor, null, ct);
            report.Written.ProjectsCreated += created.Created ? 1 : 0;
            target.Id = created.Value.Id;
            return created.Value.Id;
        }

        foreach (var row in teamRows.Values)
            row.ProjectExists = targets[row.ProjectKey].Id is not null;

        // ---- Epics → features ------------------------------------------------------------------
        var epicRecords = await imports.ListImportRecordsAsync(ImportSources.Shortcut, "epic", ct);
        var reconcileRecords = await imports.ListImportRecordsAsync(ImportSources.Shortcut, "reconcile", ct);
        var storyRecords = await imports.ListImportRecordsAsync(ImportSources.Shortcut, "story", ct);
        var epicProject = new Dictionary<long, string>();
        var featureIds = new Dictionary<long, Guid>();
        foreach (var (externalId, record) in epicRecords)
            featureIds[long.Parse(externalId, CultureInfo.InvariantCulture)] = record.EntityId;

        var epics = await source.ListEpicsAsync(ct);
        foreach (var listed in epics)
        {
            ct.ThrowIfCancellationRequested();
            var teamRow = TeamRow(listed.TeamIds.FirstOrDefault());
            epicProject[listed.Id] = epicRecords.TryGetValue(Id(listed.Id), out var known) ? KeyOf(targets, known.ProjectId) ?? teamRow.ProjectKey : teamRow.ProjectKey;
            if (updatedSince is { } since && listed.UpdatedAt < since && !retried.Contains("epic-" + Id(listed.Id)))
                continue;
            report.Epics.Seen++;
            if (listed.Archived)
            {
                report.Epics.ArchivedSkipped++;
                continue;
            }

            try
            {
                var epic = await source.GetEpicAsync(listed.Id, ct) ?? listed;
                var projectKey = epicProject[listed.Id];
                var documents = epic.RelatedDocuments > 0 || listed.RelatedDocuments > 0 ? await source.ListEpicDocumentsAsync(epic.Id, ct) : [];
                teamRow.Epics++;
                report.Epics.Mapped++;
                if (epic.Done) report.Epics.Done++; else report.Epics.Open++;
                report.Project(projectKey).Features++;
                if (!epic.Done)
                    report.ReconcileTasks++;
                report.Documents += documents.Count;
                if (mode != ImportMode.Import)
                    continue;

                var target = targets[projectKey];
                var spec = new ImportedFeatureSpec(ImportSources.Shortcut, Id(epic.Id), await EnsureProjectAsync(target), epic.Name.Trim().Length == 0 ? $"Epic {epic.Id}" : epic.Name.Trim(),
                    epic.Description, ShortcutMapper.EpicDocuments(epic, documents, members), !epic.Done, ShortcutMapper.EpicImported(epic, members, teams),
                    epic.CreatedAt, epic.UpdatedAt, "");
                var written = await imports.UpsertImportedFeatureAsync(spec with { ContentHash = ShortcutMapper.Hash(spec) }, Actor, ct);
                featureIds[epic.Id] = written.Id;
                Count(report, written.Outcome, feature: true);

                // Open features get a reconcile task, once: it is the agent's to finish, and a re-run never resets it.
                if (!epic.Done && !reconcileRecords.ContainsKey(Id(epic.Id)))
                {
                    var task = new ImportedItemSpec(ImportSources.Shortcut, "reconcile", Id(epic.Id), target.Id!.Value, written.Id,
                        $"Reconcile the requirements of {written.Key} (imported from Shortcut epic {epic.Id})", WorkItemType.Task, WorkStatus.Ready,
                        WorkPriority.Medium, ShortcutMapper.ReconcileDescription(written.Key, epic.Id), ["import-reconcile"], null, [], [], [], [],
                        new ImportedView(ImportSources.Shortcut, "sc-epic-" + Id(epic.Id), epic.AppUrl), epic.CreatedAt, epic.UpdatedAt, 0, "reconcile");
                    await imports.UpsertImportedItemAsync(task, Actor, ct);
                    report.Written.ReconcileTasksCreated++;
                }
            }
            catch (Exception exception) when (IsItemFailure(exception, ct))
            {
                report.Written.Failures++;
                report.FailedIds.Add("epic-" + Id(listed.Id));
                report.AddWarning($"Epic {listed.Id}: {exception.Message}");
            }
        }

        // ---- Stories → items -------------------------------------------------------------------
        var summaries = (await source.ListStoriesAsync(updatedSince, ct)).ToList();
        // A delta reads again what the previous run failed on.
        foreach (var failed in retried.Where(id => id.StartsWith("sc-", StringComparison.Ordinal)))
            if (long.TryParse(failed["sc-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var retryId) && summaries.All(summary => summary.Id != retryId))
                summaries.Add(new ShortcutStorySummary(retryId, DateTime.UnixEpoch));
        foreach (var warning in source.Warnings)
            report.AddWarning(warning);
        if (updatedSince is null)
            report.Stories.ArchivedSkipped = await source.CountArchivedStoriesAsync(ct);
        var storyProject = new Dictionary<long, string>();
        foreach (var (externalId, record) in storyRecords)
            if (KeyOf(targets, record.ProjectId) is { } key)
                storyProject[long.Parse(externalId, CultureInfo.InvariantCulture)] = key;
        var pending = new List<(long Id, IReadOnlyList<long> Blockers, IReadOnlyList<(string Verb, long Other)> Relations)>();
        var processed = 0;
        foreach (var summary in summaries)
        {
            ct.ThrowIfCancellationRequested();
            if (++processed % 100 == 0 && progress is not null)
                await progress(report);
            ShortcutStory? story;
            try
            {
                story = await source.GetStoryAsync(summary.Id, ct);
            }
            catch (Exception exception) when (IsItemFailure(exception, ct))
            {
                report.Written.Failures++;
                report.FailedIds.Add(ShortcutMapper.Alias(summary.Id));
                report.AddWarning($"sc-{summary.Id}: {exception.Message}");
                continue;
            }

            if (story is null)
            {
                report.AddWarning($"sc-{summary.Id} was listed but could not be read.");
                continue;
            }

            report.Stories.Seen++;
            if (story.Archived)
            {
                report.Stories.ArchivedSkipped++;
                continue;
            }

            var mapped = ShortcutMapper.Map(story, states, members, teams);
            var teamRow = TeamRow(story.TeamId ?? (story.EpicId is { } epicId ? epics.FirstOrDefault(epic => epic.Id == epicId)?.TeamIds.FirstOrDefault() : null));
            // An item goes where its feature is, so the story keeps its epic.
            var projectKey = storyRecords.TryGetValue(Id(story.Id), out var known) && KeyOf(targets, known.ProjectId) is { } knownKey ? knownKey
                : story.EpicId is { } epicOf && epicProject.TryGetValue(epicOf, out var featureProject) ? featureProject
                : teamRow.ProjectKey;
            storyProject[story.Id] = projectKey;
            teamRow.Stories++;
            report.Stories.Mapped++;
            if (mapped.Status is WorkStatus.Complete or WorkStatus.Closed) report.Stories.Done++; else report.Stories.Open++;
            var counts = report.Project(projectKey);
            counts.Items++;
            Increment(counts.ByStatus, WorkStatuses.Name(mapped.Status));
            Increment(counts.ByType, WorkJson.Name(mapped.Type));
            report.Comments += story.Comments.Count;
            report.Links += mapped.Links.Count;
            report.AcceptanceCriteria += mapped.AcceptanceCriteria.Count;
            report.Subtasks += story.SubTaskIds.Count;
            report.Relations += mapped.Relations.Count;
            if (mapped.BlockedWithoutEdges)
                report.BlockedWithoutEdges++;
            report.Attachments.Files += story.Files.Count;
            report.Attachments.TotalBytes += story.Files.Sum(file => file.Size);
            report.Attachments.LinkedFiles += story.LinkedFiles.Count;
            foreach (var file in story.Files.Where(file => file.Size > options.Value.MaxAttachmentBytes))
            {
                report.Attachments.OverLimit++;
                report.Attachments.OverLimitBytes += file.Size;
            }

            var target = targets[projectKey];
            var repository = ShortcutMapper.ChooseRepository(mapped.RepositoryCandidates, target.Owned);
            if (repository is null)
            {
                report.NoRepositoryCount++;
                if (report.NoRepository.Count < ListedLimit)
                    report.NoRepository.Add(ShortcutMapper.Alias(story.Id));
            }

            pending.Add((story.Id, mapped.Blockers, mapped.Relations));
            if (mode != ImportMode.Import)
                continue;

            try
            {
                var spec = new ImportedItemSpec(ImportSources.Shortcut, "story", Id(story.Id), await EnsureProjectAsync(target),
                    story.EpicId is { } featureEpic && featureIds.TryGetValue(featureEpic, out var featureId) ? featureId : null,
                    mapped.Title, mapped.Type, mapped.Status, WorkPriority.Medium, mapped.Description, mapped.Labels, repository,
                    mapped.AcceptanceCriteria, mapped.Comments, mapped.Links, mapped.Aliases, mapped.Imported, mapped.CreatedAt, mapped.UpdatedAt, 0, "",
                    mapped.HistoricalLinks);
                var written = await imports.UpsertImportedItemAsync(spec with { ContentHash = ShortcutMapper.Hash(spec) }, Actor, ct);
                Count(report, written.Outcome, feature: false);
                foreach (var warning in written.Warnings ?? [])
                    report.AddWarning($"sc-{story.Id}: {warning}");
                await StoreFilesAsync(source, story, written.Id, report, ct);
            }
            catch (Exception exception) when (IsItemFailure(exception, ct))
            {
                report.Written.Failures++;
                report.FailedIds.Add(ShortcutMapper.Alias(story.Id));
                report.AddWarning($"sc-{story.Id}: {exception.Message}");
            }
        }

        // ---- Relations: blocked_by edges, unmapped relations -----------------------------------
        if (mode == ImportMode.Import)
            storyRecords = await imports.ListImportRecordsAsync(ImportSources.Shortcut, "story", ct);
        foreach (var (storyId, blockers, relations) in pending)
        {
            foreach (var (verb, other) in relations.Where(relation => !storyProject.ContainsKey(relation.Other)))
                report.UnmappedRelations.Add(new ImportRelationIssue(ShortcutMapper.Alias(storyId), verb, ShortcutMapper.Alias(other), "not_imported"));

            var mappedBlockers = new List<Guid>();
            foreach (var blocker in blockers)
            {
                if (!storyProject.TryGetValue(blocker, out var blockerProject))
                {
                    report.UnmappedRelations.Add(new ImportRelationIssue(ShortcutMapper.Alias(storyId), "blocked_by", ShortcutMapper.Alias(blocker), "not_imported"));
                    continue;
                }

                if (blockerProject != storyProject[storyId])
                {
                    report.UnmappedRelations.Add(new ImportRelationIssue(ShortcutMapper.Alias(storyId), "blocked_by", ShortcutMapper.Alias(blocker), "other_project"));
                    continue;
                }

                if (mode == ImportMode.Import && storyRecords.TryGetValue(Id(blocker), out var blockerRecord))
                    mappedBlockers.Add(blockerRecord.EntityId);
            }

            if (mode != ImportMode.Import || !storyRecords.TryGetValue(Id(storyId), out var record))
                continue;
            if (mappedBlockers.Count == 0 && record.Edges.Count == 0 && !record.HasRelations)
                continue;
            try
            {
                var sync = await imports.SyncImportedDependenciesAsync(ImportSources.Shortcut, "story", Id(storyId), mappedBlockers, Actor, ct);
                report.Written.DependenciesAdded += sync.Added;
                report.Written.DependenciesRemoved += sync.Removed;
                foreach (var cycle in sync.Cycles)
                    report.UnmappedRelations.Add(new ImportRelationIssue(ShortcutMapper.Alias(storyId), "blocked_by", KeyOfStory(storyRecords, cycle), "cycle"));
            }
            catch (Exception exception) when (IsItemFailure(exception, ct))
            {
                report.Written.Failures++;
                report.FailedIds.Add(ShortcutMapper.Alias(storyId));
                report.AddWarning($"sc-{storyId} dependencies: {exception.Message}");
            }
        }

        // Cross-project blockers cannot be edges (dependencies stay within a project): noted on the item.
        if (mode == ImportMode.Import)
        {
            foreach (var group in report.UnmappedRelations.Where(issue => issue.Reason == "other_project").GroupBy(issue => issue.From))
            {
                if (!storyRecords.TryGetValue(group.Key["sc-".Length..], out var record))
                    continue;
                var comments = group.Select(issue => new ImportedComment($"xproject:{issue.To}", Actor.Name, WorkActor.SystemKind,
                    $"Blocked by {issue.To} in Shortcut, an item of another project: dependencies stay within a project, so this is noted, not an edge.", DateTime.UtcNow)).ToList();
                try
                {
                    report.Written.RelationComments += await imports.AddImportedCommentsAsync(record.EntityId, comments, Actor, ct);
                }
                catch (Exception exception) when (IsItemFailure(exception, ct))
                {
                    report.Written.Failures++;
                    report.FailedIds.Add(group.Key);
                    report.AddWarning($"{group.Key} cross-project notes: {exception.Message}");
                }
            }
        }

        report.Teams = teamRows.Values.OrderBy(row => row.TeamName, StringComparer.OrdinalIgnoreCase).ToList();
        if (report.UnmappedRelations.Count > ListedLimit)
        {
            report.AddWarning($"{report.UnmappedRelations.Count} unmapped relations; the first {ListedLimit} are listed.");
            report.UnmappedRelations = report.UnmappedRelations.Take(ListedLimit).ToList();
        }

        log.LogInformation("Shortcut {Mode} finished: {Epics} epics, {Stories} stories mapped; {Failures} failures", mode, report.Epics.Mapped, report.Stories.Mapped, report.Written.Failures);
        return report;
    }

    private async Task StoreFilesAsync(IShortcutSource source, ShortcutStory story, Guid itemId, ImportReport report, CancellationToken ct)
    {
        var max = options.Value.MaxAttachmentBytes;
        foreach (var file in story.Files)
        {
            var externalId = Id(file.Id);
            var state = await imports.GetAttachmentStateAsync(itemId, externalId, ct);
            // A record whose stored file went missing counts as not stored: it is downloaded again. A file
            // over the size bound is linked, not downloaded, once: it is not tried again.
            if (state is { StoragePath: { } stored } && files.Exists(stored) || state is { OverLimit: true })
                continue;
            var overLimit = new NewAttachmentSpec(externalId, file.Name, file.ContentType, file.Size, null, null, file.Url,
                $"{AttachmentState.OverLimitPrefix} ({max} bytes): linked, not downloaded.", file.CreatedAt);
            if (file.Size > max)
            {
                await imports.AddAttachmentAsync(itemId, overLimit, Actor, ct);
                continue;
            }

            try
            {
                // Null: the file turned out larger than the bound (by its length, or mid-stream). A failed
                // download throws and is retried by the next delta.
                var bytes = await source.DownloadFileAsync(file.Url, max, ct);
                if (bytes is null)
                {
                    report.Attachments.OverLimit++;
                    await imports.AddAttachmentAsync(itemId, overLimit, Actor, ct);
                    continue;
                }

                var (path, size) = await files.SaveAsync(bytes, ct);
                await imports.AddAttachmentAsync(itemId, new NewAttachmentSpec(externalId, file.Name, file.ContentType, size,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), path, file.Url, null, file.CreatedAt), Actor, ct);
                report.Attachments.Stored++;
                report.Written.AttachmentsStored++;
            }
            catch (Exception exception) when (IsItemFailure(exception, ct))
            {
                report.Attachments.Failed++;
                report.FailedIds.Add(ShortcutMapper.Alias(story.Id));
                report.AddWarning($"sc-{story.Id} file {file.Id}: {exception.Message}");
            }
        }
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string? KeyOf(Dictionary<string, ProjectTarget> targets, Guid projectId) =>
        targets.Values.FirstOrDefault(target => target.Id == projectId)?.Key;

    private static string KeyOfStory(IReadOnlyDictionary<string, ImportRecord> records, Guid itemId) =>
        records.FirstOrDefault(pair => pair.Value.EntityId == itemId).Key is { } id ? "sc-" + id : itemId.ToString();

    private static void Increment(SortedDictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private static void Count(ImportReport report, ImportOutcome outcome, bool feature)
    {
        var written = report.Written;
        switch (outcome, feature)
        {
            case (ImportOutcome.Created, true): written.FeaturesCreated++; break;
            case (ImportOutcome.Updated, true): written.FeaturesUpdated++; break;
            case (ImportOutcome.Unchanged, true): written.FeaturesUnchanged++; break;
            case (ImportOutcome.Created, false): written.ItemsCreated++; break;
            case (ImportOutcome.Updated, false): written.ItemsUpdated++; break;
            default: written.ItemsUnchanged++; break;
        }
    }
}
