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
    /// <summary>The sc-N alias prefix, never proposed as a project key.</summary>
    public const string ReservedKey = "SC";
    private const int ListedLimit = 500;

    private readonly ILogger log = logger ?? (ILogger)NullLogger.Instance;
    private static readonly WorkActor Actor = ImportSources.Actor(ImportSources.Shortcut);

    private sealed class ProjectTarget(string key)
    {
        public string Key { get; } = key;
        public Guid? Id { get; set; }
        /// <summary>Whether the project existed when the run started (a dry run's "exists" and the import's agree).</summary>
        public bool Existed { get; set; }
        /// <summary>The owned repositories items choose from: the project's, and (once attached, or on a dry run) the table's.</summary>
        public IReadOnlyList<string> Owned { get; set; } = [];
        /// <summary>The table's repositories the project does not own yet: attached before any item is written (sc-27928).</summary>
        public List<string> Attach { get; } = [];
        /// <summary>The table's repositories the project owns, or got now, not yet recorded as applied to it.</summary>
        public List<string> Applied { get; } = [];
    }

    /// <summary>A repository the stories name, tallied for the report (sc-27928).</summary>
    private sealed class Mention(string name)
    {
        public string Name { get; } = name;
        public int Stories { get; set; }
        public Dictionary<string, int> ByProject { get; } = new(StringComparer.Ordinal);
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
        // A proposed key is new: not an existing project's (unless the team is named like it), not the holding
        // project's, not another proposal's (sc-27931: names cut to ten characters can collide).
        // SC is the prefix of the sc-N aliases: a project keyed SC would make SC-N item keys and aliases ambiguous.
        var used = projects.Select(project => project.Key).Append(DefaultHoldingProject).Append(ReservedKey).ToHashSet(StringComparer.Ordinal);
        var mappings = teams.OrderBy(team => team.Name, StringComparer.OrdinalIgnoreCase).Select(team =>
        {
            var existing = projects.FirstOrDefault(project => project.Name.Equals(team.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return new ImportTeamMapping(team.Id, existing.Key, team.Name, existing.Name);
            var key = ShortcutMapper.UniqueKey(ShortcutMapper.ProposedKey(team.Name), used.Contains);
            used.Add(key);
            return new ImportTeamMapping(team.Id, key, team.Name, team.Name);
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
                target.Existed = true;
                target.Owned = OwnedOf(project);
            }
        }

        // ---- The table's repositories (sc-27928) ------------------------------------------------
        // Only repositories CodeGraph indexes, and none another project owns (a repository has one owner): the
        // table was checked when it was saved, and is checked again here against what is true now. A repository
        // is applied to a project once: one the project owned, or the import attached, is recorded, so a
        // repository the owner later takes away from the project is not attached again by the next sync.
        var index = new RepositoryIndex(await imports.ListIndexedRepositoriesAsync(ct));
        foreach (var group in settings.Teams.Where(row => row.Repositories is { Count: > 0 } && targets.ContainsKey(row.ProjectKey)).GroupBy(row => row.ProjectKey))
        {
            var target = targets[group.Key];
            var applied = target.Id is { } projectId ? await imports.ListAppliedRepositoriesAsync(ImportSources.Shortcut, projectId, ct) : new HashSet<string>();
            foreach (var name in group.SelectMany(row => row.Repositories!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (index.Named(name) is not { } repository)
                    report.AddWarning($"The table gives {group.Key} the repository '{name}', but CodeGraph indexes no repository by that name: it is not attached.");
                else if (target.Owned.Contains(repository.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!applied.Contains(repository.Name))
                        target.Applied.Add(repository.Name);
                }
                else if (applied.Contains(repository.Name))
                    report.AddWarning($"The table gives {group.Key} the repository {repository.Name}, which the import attached before and the project no longer owns: it is not attached again.");
                else if (repository.OwnedBy is { } owner && owner != target.Key)
                    report.AddWarning($"The table gives {group.Key} the repository {repository.Name}, but {owner} owns it (a repository is owned by one project): it is not attached.");
                else if (!target.Attach.Contains(repository.Name, StringComparer.OrdinalIgnoreCase))
                    target.Attach.Add(repository.Name);
            }
        }

        foreach (var target in targets.Values.Where(target => target.Attach.Count > 0 || target.Applied.Count > 0))
        {
            // A dry run reports against the repositories the import will attach, and a project the import creates
            // is created with them.
            if (mode != ImportMode.Import || target.Id is null)
            {
                target.Owned = [.. target.Owned, .. target.Attach];
                continue;
            }

            // An existing project gets them now, before any item is written.
            if (target.Attach.Count > 0 && await work.GetProjectAsync(target.Id.Value, ct) is { } project)
            {
                try
                {
                    var updated = await work.UpdateProjectAsync(project.Id, project.Version, new ProjectPatchSpec(Repositories:
                        [.. project.Repositories, .. target.Attach.Select(name => new RepositoryMemberView(name, RepositoryRole.Owned))]), Actor, ct);
                    report.Written.RepositoriesAttached += target.Attach.Count;
                    target.Owned = OwnedOf(updated);
                    target.Applied.AddRange(target.Attach);
                }
                catch (WorkException exception)
                {
                    // The report says what the project owns: what it owned before, not the table's.
                    report.AddWarning($"{target.Key}: the table's repositories {string.Join(", ", target.Attach)} were not attached: {exception.Message}");
                }

                target.Attach.Clear();
            }

            await imports.RecordAppliedRepositoriesAsync(ImportSources.Shortcut, target.Id.Value, target.Applied, ct);
        }

        // A project of the table that does not exist yet is created (with the table's repositories) when the first
        // work for it is written.
        async Task<Guid> EnsureProjectAsync(ProjectTarget target)
        {
            if (target.Id is { } existing)
                return existing;
            var name = settings.Teams.FirstOrDefault(row => row.ProjectKey == target.Key)?.ProjectName ?? target.Key;
            WorkCreated<ProjectView> created;
            try
            {
                created = await work.CreateProjectAsync(new NewProjectSpec(target.Key, name, "Created by the Shortcut import (sc-27876).", [],
                    target.Attach.Select(repository => new RepositoryMemberView(repository, RepositoryRole.Owned)).ToList()), Actor, null, ct);
                report.Written.RepositoriesAttached += created.Created ? target.Attach.Count : 0;
            }
            catch (WorkException exception) when (target.Attach.Count > 0)
            {
                // Another project took one of them since the run started: the project is created without them.
                report.AddWarning($"{target.Key}: the table's repositories {string.Join(", ", target.Attach)} were not attached: {exception.Message}");
                created = await work.CreateProjectAsync(new NewProjectSpec(target.Key, name, "Created by the Shortcut import (sc-27876).", [], []), Actor, null, ct);
            }

            report.Written.ProjectsCreated += created.Created ? 1 : 0;
            target.Id = created.Value.Id;
            target.Owned = OwnedOf(created.Value);
            target.Attach.Clear();
            await imports.RecordAppliedRepositoriesAsync(ImportSources.Shortcut, created.Value.Id, target.Owned, ct);
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
            // An archived epic never imported is skipped; one imported before is flagged (sc-27929).
            var archived = listed.Archived;
            if (archived && known is null)
            {
                report.Epics.ArchivedSkipped++;
                continue;
            }

            try
            {
                var epic = await source.GetEpicAsync(listed.Id, ct) ?? listed;
                var projectKey = epicProject[listed.Id];
                var documents = epic.RelatedDocuments > 0 || listed.RelatedDocuments > 0 ? await source.ListEpicDocumentsAsync(epic.Id, ct) : [];
                if (archived)
                {
                    report.Epics.ArchivedClosed++;
                }
                else
                {
                    teamRow.Epics++;
                    Increment(teamRow.EpicsByProject, projectKey);
                    report.Epics.Mapped++;
                    if (epic.Done) report.Epics.Done++; else report.Epics.Open++;
                    report.Project(projectKey).Features++;
                    if (!epic.Done)
                        report.ReconcileTasks++;
                    report.Documents += documents.Count;
                }

                if (mode != ImportMode.Import)
                    continue;

                var target = targets[projectKey];
                var imported = ShortcutMapper.EpicImported(epic, members, teams);
                var spec = new ImportedFeatureSpec(ImportSources.Shortcut, Id(epic.Id), await EnsureProjectAsync(target), epic.Name.Trim().Length == 0 ? $"Epic {epic.Id}" : epic.Name.Trim(),
                    epic.Description, ShortcutMapper.EpicDocuments(epic, documents, members), !epic.Done, archived ? imported with { Archived = true } : imported,
                    epic.CreatedAt, epic.UpdatedAt, "");
                // Whether the feature was flagged archived before this write: only a change of that moves its reconcile task.
                var wasArchived = known is not null && (await work.GetFeatureAsync(known.EntityId, ct))?.Imported?.Archived == true;
                var written = await imports.UpsertImportedFeatureAsync(spec with { ContentHash = ShortcutMapper.Hash(spec) }, Actor, ct);
                featureIds[epic.Id] = written.Id;
                Count(report, written.Outcome, feature: true);
                var reconcile = reconcileRecords.GetValueOrDefault(Id(epic.Id));
                if (archived)
                {
                    // Closed when the epic becomes archived; one a person reopens after that stays open.
                    if (!wasArchived)
                        await MoveReconcileTaskAsync(reconcile, WorkStatus.Closed,
                            $"Shortcut epic {epic.Id} was archived, so its requirements are not reconciled: closed by the import.", ct);
                    continue;
                }

                // Unarchived in Shortcut: the flag went with this write; the reconcile task the archive closed is open again.
                if (wasArchived && !epic.Done && reconcile is not null)
                    await MoveReconcileTaskAsync(reconcile, WorkStatus.Ready,
                        $"Shortcut epic {epic.Id} was unarchived, so its requirements are to be reconciled again: reopened by the import.", ct);

                // Open features get a reconcile task, once: it is the agent's to finish, and a re-run never resets it.
                if (!epic.Done && reconcile is null)
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
        // A full run lists the non-archived stories only: an imported story it no longer lists is read again, so
        // one archived since is closed (sc-27929).
        var reread = new HashSet<long>();
        if (updatedSince is null)
        {
            var listedIds = summaries.Select(summary => summary.Id).ToHashSet();
            foreach (var externalId in storyRecords.Keys)
                if (long.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var importedId) && !listedIds.Contains(importedId) && reread.Add(importedId))
                    summaries.Add(new ShortcutStorySummary(importedId, DateTime.UnixEpoch));
        }

        foreach (var warning in source.Warnings)
            report.AddWarning(warning);
        var archivedCount = updatedSince is null ? await source.CountArchivedStoriesAsync(ct) : 0;
        var mentions = new Dictionary<string, Mention>(StringComparer.OrdinalIgnoreCase);
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
            // An archived story a delta lists that was never imported is skipped without reading it.
            if (summary.Archived && !storyRecords.ContainsKey(Id(summary.Id)) && !retried.Contains(ShortcutMapper.Alias(summary.Id)))
            {
                report.Stories.Seen++;
                report.Stories.ArchivedSkipped++;
                continue;
            }

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
                report.AddWarning(reread.Contains(summary.Id)
                    ? $"sc-{summary.Id} was imported, but Shortcut no longer has it: its item was left as it is."
                    : $"sc-{summary.Id} was listed but could not be read.");
                continue;
            }

            report.Stories.Seen++;
            var record = storyRecords.GetValueOrDefault(Id(story.Id));
            var archived = story.Archived;
            if (archived && record is null)
            {
                report.Stories.ArchivedSkipped++;
                continue;
            }

            var mapped = ShortcutMapper.Map(story, states, members, teams);
            var teamRow = TeamRow(story.TeamId ?? (story.EpicId is { } epicId ? epics.FirstOrDefault(epic => epic.Id == epicId)?.TeamIds.FirstOrDefault() : null));
            // An item goes where its feature is, so the story keeps its epic.
            var projectKey = record is not null && KeyOf(targets, record.ProjectId) is { } knownKey ? knownKey
                : story.EpicId is { } epicOf && epicProject.TryGetValue(epicOf, out var featureProject) ? featureProject
                : teamRow.ProjectKey;
            storyProject[story.Id] = projectKey;
            var target = targets[projectKey];
            var repository = ChooseRepository(mapped.RepositoryCandidates, target.Owned, index);

            // Imported, then archived in Shortcut (sc-27929): the item is closed (a finished one stays finished).
            if (archived)
            {
                report.Stories.ArchivedClosed++;
                if (mode != ImportMode.Import)
                    continue;
                try
                {
                    var current = await work.GetItemAsync(record!.EntityId, false, ct);
                    await WriteStoryAsync(source, story, ShortcutMapper.Archived(mapped, current?.Status, story.UpdatedAt), await EnsureProjectAsync(target), repository, featureIds, report, ct);
                }
                catch (Exception exception) when (IsItemFailure(exception, ct))
                {
                    report.Written.Failures++;
                    report.FailedIds.Add(ShortcutMapper.Alias(story.Id));
                    report.AddWarning($"sc-{story.Id}: {exception.Message}");
                }

                continue;
            }

            teamRow.Stories++;
            Increment(teamRow.StoriesByProject, projectKey);
            foreach (var named in mapped.RepositoryCandidates)
            {
                if (!mentions.TryGetValue(named, out var mention))
                    mentions[named] = mention = new Mention(named);
                mention.Stories++;
                Increment(mention.ByProject, projectKey);
            }
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
                await WriteStoryAsync(source, story, mapped, await EnsureProjectAsync(target), repository, featureIds, report, ct);
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

        if (updatedSince is null)
            report.Stories.ArchivedSkipped = Math.Max(0, archivedCount - report.Stories.ArchivedClosed);
        Summarize(report, settings, targets, teamRows, mentions, index);
        report.Teams = teamRows.Values.OrderBy(row => row.TeamName, StringComparer.OrdinalIgnoreCase).ToList();
        if (report.UnmappedRelations.Count > ListedLimit)
        {
            report.AddWarning($"{report.UnmappedRelations.Count} unmapped relations; the first {ListedLimit} are listed.");
            report.UnmappedRelations = report.UnmappedRelations.Take(ListedLimit).ToList();
        }

        log.LogInformation("Shortcut {Mode} finished: {Epics} epics, {Stories} stories mapped; {Failures} failures", mode, report.Epics.Mapped, report.Stories.Mapped, report.Written.Failures);
        return report;
    }

    private static List<string> OwnedOf(ProjectView project) =>
        project.Repositories.Where(member => member.Role == RepositoryRole.Owned).Select(member => member.Repository).ToList();

    /// <summary>The item's repository: what the story names, as CodeGraph indexes it, among the project's owned repositories.</summary>
    private static string? ChooseRepository(IReadOnlyList<string> candidates, IReadOnlyList<string> owned, RepositoryIndex index) =>
        ShortcutMapper.ChooseRepository(candidates.Select(candidate => index.Resolve(candidate)?.Name).OfType<string>().Concat(candidates)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(), owned);

    private async Task WriteStoryAsync(IShortcutSource source, ShortcutStory story, MappedStory mapped, Guid projectId, string? repository,
        IReadOnlyDictionary<long, Guid> featureIds, ImportReport report, CancellationToken ct)
    {
        var spec = new ImportedItemSpec(ImportSources.Shortcut, "story", Id(story.Id), projectId,
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

    /// <summary>
    /// The reconcile task of a feature whose epic was archived (closed, when open) or unarchived (ready again, when the
    /// archive closed it): a finished task is never reopened but a closed one, and a complete one is left alone.
    /// </summary>
    private async Task MoveReconcileTaskAsync(ImportRecord? record, WorkStatus status, string comment, CancellationToken ct)
    {
        if (record is null || await work.GetItemAsync(record.EntityId, false, ct) is not { } task)
            return;
        var moves = status == WorkStatus.Closed ? !WorkStatuses.IsFinished(task.Status) : task.Status == WorkStatus.Closed;
        if (moves)
            await work.UpdateItemAsync(task.Id, task.Version, new ItemPatchSpec(Status: status, Comment: comment), Actor, ct);
    }

    /// <summary>
    /// The report's tables: each project's existence and repositories, the repositories the stories name and which
    /// project each would go to (sc-27928), each team's proposed repositories, and the teams that share a project (sc-27931).
    /// </summary>
    private static void Summarize(ImportReport report, ImportSettings settings, Dictionary<string, ProjectTarget> targets, Dictionary<string, ImportTeamRow> teamRows,
        Dictionary<string, Mention> mentions, RepositoryIndex index)
    {
        foreach (var (key, counts) in report.Projects)
        {
            if (!targets.TryGetValue(key, out var target))
                continue;
            counts.Exists = target.Existed;
            // What the project owns after this run's writes (on a dry run: would own).
            counts.Repositories = target.Owned.ToList();
        }

        // Which project a repository is proposed for: none when a project owns it already; else the one the table gives
        // it to; else the project whose stories name it most. Several owner/repo names can be one indexed repository.
        var table = settings.Teams.Where(row => row.Repositories is not null)
            .SelectMany(row => row.Repositories!.Select(name => (Repository: index.Named(name)?.Name, row.ProjectKey)))
            .Where(pair => pair.Repository is not null)
            .GroupBy(pair => pair.Repository!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ProjectKey, StringComparer.OrdinalIgnoreCase);
        var resolved = mentions.Values.Select(mention => (Mention: mention, Repository: index.Resolve(mention.Name))).ToList();
        var proposedFor = resolved.Where(pair => pair.Repository is { OwnedBy: null })
            .GroupBy(pair => pair.Repository!.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => table.GetValueOrDefault(group.Key) ?? group
                .SelectMany(pair => pair.Mention.ByProject)
                .GroupBy(count => count.Key, StringComparer.Ordinal)
                .Select(project => (Key: project.Key, Stories: project.Sum(count => count.Value)))
                .OrderByDescending(project => project.Stories).ThenBy(project => project.Key, StringComparer.Ordinal)
                .First().Key, StringComparer.OrdinalIgnoreCase);
        report.RepositoryMentions = resolved
            .OrderByDescending(pair => pair.Mention.Stories).ThenBy(pair => pair.Mention.Name, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .Select(pair => new ImportRepositoryMention(pair.Mention.Name, pair.Mention.Stories, pair.Repository?.Name, pair.Repository?.OwnedBy,
                pair.Repository is { } repository ? proposedFor.GetValueOrDefault(repository.Name) : null))
            .ToList();

        var namedMost = resolved.Where(pair => pair.Repository is not null)
            .GroupBy(pair => pair.Repository!.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Mention.Stories), StringComparer.OrdinalIgnoreCase);
        var tableRows = settings.Teams.ToDictionary(row => row.TeamKey, StringComparer.Ordinal);
        foreach (var row in teamRows.Values)
        {
            row.Repositories = tableRows.GetValueOrDefault(row.TeamKey)?.Repositories?.ToList();
            var target = targets.GetValueOrDefault(row.ProjectKey);
            row.ProposedRepositories = proposedFor.Where(pair => pair.Value == row.ProjectKey)
                .Select(pair => pair.Key)
                .Where(name => target is null || !target.Owned.Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(name => namedMost.GetValueOrDefault(name)).ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Two teams mapped to one project (other than the holding project): flagged, for the owner to confirm.
        foreach (var group in teamRows.Values.Where(row => row.ProjectKey != settings.HoldingProjectKey).GroupBy(row => row.ProjectKey).Where(group => group.Count() > 1))
        {
            foreach (var row in group)
                row.SharesProjectWith = group.Where(other => other != row).Select(other => other.TeamName ?? other.TeamKey).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            report.AddWarning($"Teams {string.Join(", ", group.Select(row => row.TeamName ?? row.TeamKey).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))} all map to project {group.Key}. " +
                "If they belong apart, give each its own project key before approving.");
        }
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

    private static void Increment(IDictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;

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
