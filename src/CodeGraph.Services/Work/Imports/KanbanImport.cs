using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work.Imports;

// The KanbanBoard import (design §10.2, §5.2a "Existing KanbanBoard data"): only the epic
// "SceneWorks Code Review 2026-10-27". Epic → feature with its description and documents; Issue → bug,
// Task → task; Backlog/Ready/InProgress/Done/Closed → backlog/ready/in_progress/complete/closed;
// claims not carried over; comments, links and dependencies carried over. KanbanBoard is only read.

public sealed record KanbanEpic(string Id, string ProjectId, string Name, string? Description, bool Archived, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record KanbanComment(string Id, string Author, string Body, DateTime CreatedAt);

public sealed record KanbanLink(string Kind, string Url);

public sealed record KanbanItem(
    string Id,
    string Title,
    string? Description,
    string Type,
    string Status,
    string Priority,
    IReadOnlyList<string> Labels,
    IReadOnlyList<KanbanComment> Comments,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<KanbanLink> Links,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Url);

public interface IKanbanSource
{
    Task<KanbanEpic?> GetEpicAsync(string epicId, CancellationToken ct);
    Task<IReadOnlyList<ShortcutDocument>> ListEpicDocumentsAsync(string epicId, CancellationToken ct);
    /// <summary>Every item of the epic with its comments, blockers and links.</summary>
    Task<IReadOnlyList<KanbanItem>> ListItemsAsync(string epicId, CancellationToken ct);
}

public static class KanbanPayload
{
    public static KanbanEpic Epic(JsonElement e) =>
        new(ShortcutPayload.Str(e, "id") ?? "", ShortcutPayload.Str(e, "projectId") ?? "", ShortcutPayload.Str(e, "name") ?? "", ShortcutPayload.Str(e, "description"),
            ShortcutPayload.Bool(e, "isArchived"), ShortcutPayload.Time(e, "createdAtUtc") ?? DateTime.UnixEpoch, ShortcutPayload.Time(e, "updatedAtUtc") ?? DateTime.UnixEpoch);

    public static ShortcutDocument Document(JsonElement e) =>
        new(ShortcutPayload.Str(e, "id") ?? "", ShortcutPayload.Str(e, "title") ?? "Document", ShortcutPayload.Str(e, "body") ?? "");

    public static KanbanItem Item(JsonElement e)
    {
        IEnumerable<JsonElement> Array(string name) =>
            e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
        var labels = Array("label_list").Select(label => label.GetString()).OfType<string>().ToList();
        if (labels.Count == 0 && ShortcutPayload.Str(e, "labels") is { Length: > 0 } csv)
            labels = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return new KanbanItem(
            ShortcutPayload.Str(e, "id") ?? "", ShortcutPayload.Str(e, "title") ?? "", ShortcutPayload.Str(e, "description"),
            ShortcutPayload.Str(e, "type") ?? "Task", ShortcutPayload.Str(e, "status") ?? "Backlog", ShortcutPayload.Str(e, "priority") ?? "Medium", labels,
            Array("comments").Select(c => new KanbanComment(ShortcutPayload.Str(c, "id") ?? "", ShortcutPayload.Str(c, "author") ?? "unknown",
                ShortcutPayload.Str(c, "body") ?? "", ShortcutPayload.Time(c, "createdAtUtc") ?? DateTime.UnixEpoch)).OrderBy(c => c.CreatedAt).ToList(),
            Array("blockers").Select(b => ShortcutPayload.Str(b, "id")).OfType<string>().ToList(),
            Array("links").Select(l => new KanbanLink(ShortcutPayload.Str(l, "kind") ?? "other", ShortcutPayload.Str(l, "url") ?? "")).Where(l => l.Url.Length > 0).ToList(),
            ShortcutPayload.Time(e, "createdAtUtc") ?? DateTime.UnixEpoch, ShortcutPayload.Time(e, "updatedAtUtc") ?? DateTime.UnixEpoch, ShortcutPayload.Str(e, "url"));
    }
}

/// <summary>KanbanBoard's REST API, authenticated with a KanbanBoard PAT stored in MCP Hub credentials as kanban/apiToken.</summary>
public sealed class KanbanRestSource(HttpClient http, string token, RequestRateLimiter limiter) : IKanbanSource
{
    public const string HttpClientName = "work-import-kanban";

    public static async Task<KanbanRestSource> CreateAsync(IHttpClientFactory factory, IMcpHubStore hub, IOptions<WorkImportOptions> options, CancellationToken ct)
    {
        var token = await hub.GetCredentialValueAsync("kanban", "apiToken", ct);
        if (string.IsNullOrWhiteSpace(token))
            throw WorkException.Invalid("No KanbanBoard token is configured. Set kanban/apiToken (a read-scoped KanbanBoard PAT) in MCP Hub credentials before running the KanbanBoard import.");
        var client = factory.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(options.Value.KanbanBaseUrl);
        return new KanbanRestSource(client, token.Trim(), new RequestRateLimiter(600));
    }

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        await limiter.WaitAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"KanbanBoard answered {(int)response.StatusCode} for GET {path}.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    public async Task<KanbanEpic?> GetEpicAsync(string epicId, CancellationToken ct) =>
        await GetAsync($"api/epics/{Uri.EscapeDataString(epicId)}", ct) is { } epic ? KanbanPayload.Epic(epic) : null;

    public async Task<IReadOnlyList<ShortcutDocument>> ListEpicDocumentsAsync(string epicId, CancellationToken ct) =>
        await GetAsync($"api/epics/{Uri.EscapeDataString(epicId)}/documents", ct) is { } documents
            ? documents.EnumerateArray().Select(KanbanPayload.Document).ToList()
            : [];

    public async Task<IReadOnlyList<KanbanItem>> ListItemsAsync(string epicId, CancellationToken ct)
    {
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = (await GetAsync($"api/items?epicId={Uri.EscapeDataString(epicId)}&limit=200" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"), ct))!.Value;
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(item => ShortcutPayload.Str(item, "id")).OfType<string>());
            cursor = ShortcutPayload.Str(page, "next_cursor");
        } while (cursor is not null);

        var items = new List<KanbanItem>();
        // The item read carries its comments, blockers and links.
        foreach (var id in ids)
            if (await GetAsync($"api/items/{id}", ct) is { } item)
                items.Add(KanbanPayload.Item(item));
        return items;
    }
}

public sealed class RecordedKanbanSource : IKanbanSource
{
    public Dictionary<string, KanbanEpic> Epics { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<ShortcutDocument>> Documents { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<KanbanItem>> Items { get; } = new(StringComparer.Ordinal);

    public static RecordedKanbanSource FromDirectory(string directory)
    {
        static JsonElement Read(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }

        var source = new RecordedKanbanSource();
        var epic = KanbanPayload.Epic(Read(Path.Combine(directory, "epic.json")));
        source.Epics[epic.Id] = epic;
        source.Documents[epic.Id] = Read(Path.Combine(directory, "documents.json")).EnumerateArray().Select(KanbanPayload.Document).ToList();
        source.Items[epic.Id] = Directory.EnumerateFiles(Path.Combine(directory, "items"), "*.json").Select(path => KanbanPayload.Item(Read(path))).ToList();
        return source;
    }

    public Task<KanbanEpic?> GetEpicAsync(string epicId, CancellationToken ct) => Task.FromResult(Epics.GetValueOrDefault(epicId));

    public Task<IReadOnlyList<ShortcutDocument>> ListEpicDocumentsAsync(string epicId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ShortcutDocument>>(Documents.GetValueOrDefault(epicId) ?? []);

    public Task<IReadOnlyList<KanbanItem>> ListItemsAsync(string epicId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<KanbanItem>>(Items.GetValueOrDefault(epicId) ?? []);
}

public sealed class KanbanImporter(IWorkStore work, IWorkImportStore imports)
{
    /// <summary>The one KanbanBoard epic that is imported (D23).</summary>
    public const string CodeReviewEpicId = "25860fbc-3b64-4c44-8dea-7e1bc8723a7c";
    public const string DefaultProject = "SW";
    private static readonly WorkActor Actor = ImportSources.Actor(ImportSources.Kanban);

    public static WorkItemType Type(string type) => type.ToLowerInvariant() switch
    {
        "issue" => WorkItemType.Bug,
        "story" => WorkItemType.Story,
        _ => WorkItemType.Task
    };

    public static WorkStatus Status(string status) => status.ToLowerInvariant() switch
    {
        "ready" => WorkStatus.Ready,
        "inprogress" or "in_progress" => WorkStatus.InProgress,
        "done" => WorkStatus.Complete,
        "closed" => WorkStatus.Closed,
        // Blocked is derived in CodeGraph; a Blocked item imports as Backlog (D26).
        _ => WorkStatus.Backlog
    };

    public static WorkPriority Priority(string priority) => priority.ToLowerInvariant() switch
    {
        "critical" => WorkPriority.Critical,
        "high" => WorkPriority.High,
        "low" => WorkPriority.Low,
        _ => WorkPriority.Medium
    };

    public static WorkLinkKind LinkKind(string kind) => kind.ToLowerInvariant() switch
    {
        "pr" => WorkLinkKind.Pr,
        "branch" => WorkLinkKind.Branch,
        _ => WorkLinkKind.Other
    };

    public static (string Name, string Kind) Author(string author)
    {
        var lower = author.ToLowerInvariant();
        return lower.Contains("claude", StringComparison.Ordinal) ? ("claude", WorkActor.AgentKind)
            : lower.Contains("codex", StringComparison.Ordinal) ? ("codex", WorkActor.AgentKind)
            : (author, WorkActor.UserKind);
    }

    /// <summary>The proposed settings: the Code Review epic into the SceneWorks project (key SW), which the owner confirms.</summary>
    public static ImportSettings Propose() => new(DefaultProject, [], KanbanEpicId: CodeReviewEpicId);

    /// <param name="report">Filled as the run goes, so a failed run keeps the progress it made.</param>
    public async Task<ImportReport> RunAsync(IKanbanSource source, ImportMode mode, ImportReport report, CancellationToken ct)
    {
        var stored = await imports.GetImportSettingsAsync(ImportSources.Kanban, ct);
        var approved = stored?.ApprovedAt is not null;
        if (mode == ImportMode.Import && !approved)
            throw WorkException.Invalid("The KanbanBoard target project has not been approved (PUT /api/work/imports/kanban/settings with approve: true). Nothing was imported.");
        var settings = approved ? stored! : stored ?? Propose();
        var epicId = settings.KanbanEpicId ?? CodeReviewEpicId;
        // D23: only the Code Review epic is imported; the rest stays in KanbanBoard.
        if (epicId != CodeReviewEpicId)
            throw WorkException.Invalid($"Only the KanbanBoard epic SceneWorks Code Review 2026-10-27 ({CodeReviewEpicId}) is imported; the settings name {epicId}. Nothing was imported.");
        var projectKey = settings.HoldingProjectKey;
        report.Source = ImportSources.Kanban;
        report.Mode = mode;
        report.SettingsApproved = approved;
        report.HoldingProject = projectKey;

        var epic = await source.GetEpicAsync(epicId, ct) ?? throw WorkException.NotFound($"KanbanBoard has no epic {epicId}.");
        var documents = await source.ListEpicDocumentsAsync(epicId, ct);
        var items = await source.ListItemsAsync(epicId, ct);
        report.Teams.Add(new ImportTeamRow { TeamKey = epic.Id, TeamName = epic.Name, ProjectKey = projectKey, Mapping = approved ? "approved" : "proposed", Epics = 1, Stories = items.Count });

        var target = await work.ResolveAsync(projectKey, ct) is { Kind: WorkEntityKind.Project } found ? await work.GetProjectAsync(found.Id, ct) : null;
        report.Teams[0].ProjectExists = target is not null;
        if (target is null && mode == ImportMode.Import)
            throw WorkException.NotFound($"Project {projectKey} does not exist: create it (with its owned repositories) before importing the KanbanBoard epic.");
        var owned = target?.Repositories.Where(member => member.Role == RepositoryRole.Owned).Select(member => member.Repository).ToList() ?? [];

        report.Epics.Seen = report.Epics.Mapped = 1;
        var open = items.Any(item => Status(item.Status) is not (WorkStatus.Complete or WorkStatus.Closed));
        if (open) report.Epics.Open = 1; else report.Epics.Done = 1;
        report.Documents = documents.Count;
        report.Project(projectKey).Features = 1;
        if (open)
            report.ReconcileTasks = 1;

        Guid? featureId = null;
        if (mode == ImportMode.Import)
        {
            var feature = new ImportedFeatureSpec(ImportSources.Kanban, epic.Id, target!.Id, epic.Name, epic.Description,
                documents.Select(document => new DocumentSpec(document.Title, document.Body)).ToList(), open,
                new ImportedView(ImportSources.Kanban, epic.Id), epic.CreatedAt, epic.UpdatedAt, "");
            var written = await imports.UpsertImportedFeatureAsync(feature with { ContentHash = ShortcutMapper.Hash(feature) }, Actor, ct);
            featureId = written.Id;
            report.Written.FeaturesCreated += written.Outcome == ImportOutcome.Created ? 1 : 0;
            report.Written.FeaturesUpdated += written.Outcome == ImportOutcome.Updated ? 1 : 0;
            report.Written.FeaturesUnchanged += written.Outcome == ImportOutcome.Unchanged ? 1 : 0;
            var reconcile = await imports.ListImportRecordsAsync(ImportSources.Kanban, "reconcile", ct);
            if (open && !reconcile.ContainsKey(epic.Id))
            {
                await imports.UpsertImportedItemAsync(new ImportedItemSpec(ImportSources.Kanban, "reconcile", epic.Id, target.Id, written.Id,
                    $"Reconcile the requirements of {written.Key} (imported from KanbanBoard)", WorkItemType.Task, WorkStatus.Ready, WorkPriority.Medium,
                    ShortcutMapper.ReconcileDescription(written.Key, 0).Replace("Shortcut epic 0", "the KanbanBoard epic", StringComparison.Ordinal),
                    ["import-reconcile"], null, [], [], [], [], new ImportedView(ImportSources.Kanban, epic.Id), epic.CreatedAt, epic.UpdatedAt, 0, "reconcile"), Actor, ct);
                report.Written.ReconcileTasksCreated++;
            }
        }

        var ids = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var status = Status(item.Status);
            var comments = item.Comments.Select(comment =>
            {
                var (name, kind) = Author(comment.Author);
                return new ImportedComment(comment.Id, name, kind, comment.Body, comment.CreatedAt);
            }).ToList();
            if (item.Status.Equals("Blocked", StringComparison.OrdinalIgnoreCase) && item.Blockers.Count == 0)
            {
                report.BlockedWithoutEdges++;
                comments.Add(new ImportedComment("blocked-without-edges", Actor.Name, WorkActor.SystemKind,
                    "Imported as Backlog: KanbanBoard had this item Blocked with no blocking item (design D26).", item.UpdatedAt));
            }
            else if (item.Status.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
                status = WorkStatus.Ready;

            var links = item.Links.Select(link => new LinkSpec(LinkKind(link.Kind), link.Url)).DistinctBy(link => (link.Kind, link.Url)).ToList();
            var repository = ShortcutMapper.ChooseRepository(
                links.Select(link => link.Url).Select(url => ShortcutMapper.LinkKind(url) == WorkLinkKind.Other ? null : url).OfType<string>()
                    .Select(url => string.Join('/', new Uri(url).Segments.Skip(1).Take(2).Select(segment => segment.Trim('/')))).ToList(),
                owned);
            var counts = report.Project(projectKey);
            counts.Items++;
            counts.ByStatus[WorkStatuses.Name(status)] = counts.ByStatus.GetValueOrDefault(WorkStatuses.Name(status)) + 1;
            counts.ByType[WorkJson.Name(Type(item.Type))] = counts.ByType.GetValueOrDefault(WorkJson.Name(Type(item.Type))) + 1;
            report.Stories.Seen++;
            report.Stories.Mapped++;
            if (status is WorkStatus.Complete or WorkStatus.Closed) report.Stories.Done++; else report.Stories.Open++;
            report.Comments += item.Comments.Count;
            report.Links += links.Count;
            if (repository is null)
            {
                report.NoRepositoryCount++;
                if (report.NoRepository.Count < 500)
                    report.NoRepository.Add(item.Id);
            }

            foreach (var blocker in item.Blockers.Where(blocker => !ids.Contains(blocker)))
                report.UnmappedRelations.Add(new ImportRelationIssue(item.Id, "blocked_by", blocker, "not_imported"));
            if (mode != ImportMode.Import)
                continue;

            try
            {
                // KanbanBoard links carry no PR state: a finished item's PR links are historical.
                var historical = status is WorkStatus.Complete or WorkStatus.Closed ? links.Where(link => link.Kind == WorkLinkKind.Pr).Select(link => link.Url).ToList() : [];
                var spec = new ImportedItemSpec(ImportSources.Kanban, "item", item.Id, target!.Id, featureId, item.Title, Type(item.Type), status, Priority(item.Priority),
                    item.Description, item.Labels, repository, [], comments, links, [], new ImportedView(ImportSources.Kanban, item.Id, item.Url),
                    item.CreatedAt, item.UpdatedAt, 0, "", historical);
                var written = await imports.UpsertImportedItemAsync(spec with { ContentHash = ShortcutMapper.Hash(spec) }, Actor, ct);
                switch (written.Outcome)
                {
                    case ImportOutcome.Created: report.Written.ItemsCreated++; break;
                    case ImportOutcome.Updated: report.Written.ItemsUpdated++; break;
                    default: report.Written.ItemsUnchanged++; break;
                }
            }
            catch (Exception exception) when (ShortcutImporter.IsItemFailure(exception, ct))
            {
                report.Written.Failures++;
                report.FailedIds.Add(item.Id);
                report.AddWarning($"{item.Id}: {exception.Message}");
            }
        }

        if (mode == ImportMode.Import)
        {
            var records = await imports.ListImportRecordsAsync(ImportSources.Kanban, "item", ct);
            foreach (var item in items)
            {
                var blockers = item.Blockers.Where(records.ContainsKey).Select(blocker => records[blocker].EntityId).ToList();
                if (!records.TryGetValue(item.Id, out var record) || (blockers.Count == 0 && record.Edges.Count == 0 && !record.HasRelations))
                    continue;
                try
                {
                    var sync = await imports.SyncImportedDependenciesAsync(ImportSources.Kanban, "item", item.Id, blockers, Actor, ct);
                    report.Written.DependenciesAdded += sync.Added;
                    report.Written.DependenciesRemoved += sync.Removed;
                    foreach (var cycle in sync.Cycles)
                        report.UnmappedRelations.Add(new ImportRelationIssue(item.Id, "blocked_by", cycle.ToString(), "cycle"));
                }
                catch (Exception exception) when (ShortcutImporter.IsItemFailure(exception, ct))
                {
                    report.Written.Failures++;
                    report.FailedIds.Add(item.Id);
                    report.AddWarning($"{item.Id} dependencies: {exception.Message}");
                }
            }
        }

        return report;
    }
}
