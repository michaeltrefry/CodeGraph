using System.Buffers.Text;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public interface IWorkService
{
    /// <summary>
    /// Runs a work tool on raw JSON arguments and answers its result, or throws a
    /// <see cref="WorkException"/>. The one entry point of MCP and REST, so both refuse the same calls.
    /// </summary>
    Task<object> ExecuteAsync(string tool, JsonElement arguments, WorkActor actor, CancellationToken ct = default);
}

/// <summary>
/// The work tools: strict argument reading, the rules a call must meet before it reaches the
/// database (ported from KanbanBoard's TryNew / EpicPlans.ReadAsync / IdempotencyKeys), resolution
/// of keys and ids, and the change feed's cursor and long-poll. The store does the rest.
/// </summary>
public sealed class WorkService(IWorkStore? store = null, TimeProvider? timeProvider = null) : IWorkService
{
    private const string FindCursorKind = "fw";
    private const string ChangeCursorKind = "ch";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private IWorkStore Store => store
        ?? throw new WorkException(WorkErrorCodes.Unavailable, "Work tracking needs the MariaDB storage provider; this deployment runs without it.");

    /// <summary>The identity a write is attributed to (E3): the authenticated user, or <c>anonymous</c>.</summary>
    public static WorkActor ActorFor(ClaimsPrincipal? user)
    {
        var name = user?.FindFirst("preferred_username")?.Value
            ?? user?.FindFirst("username")?.Value
            ?? user?.FindFirst(ClaimTypes.Name)?.Value
            ?? user?.Identity?.Name;
        name = string.IsNullOrWhiteSpace(name) ? "anonymous" : name.Trim();
        return new WorkActor(name.Length <= 255 ? name : name[..255]);
    }

    public async Task<object> ExecuteAsync(string tool, JsonElement arguments, WorkActor actor, CancellationToken ct = default) => tool switch
    {
        WorkTools.CreateWork => await CreateAsync(arguments, WorkJson.Parse<CreateWorkRequest>(arguments, tool), actor, ct),
        WorkTools.GetWork => await GetAsync(WorkJson.Parse<GetWorkRequest>(arguments, tool), ct),
        WorkTools.FindWork => await FindAsync(WorkJson.Parse<FindWorkRequest>(arguments, tool), ct),
        WorkTools.UpdateWork => await UpdateAsync(WorkJson.Parse<UpdateWorkRequest>(arguments, tool), actor, ct),
        WorkTools.NextWork => await NextAsync(WorkJson.Parse<NextWorkRequest>(arguments, tool), actor, ct),
        WorkTools.Changes => await ChangesAsync(WorkJson.Parse<ChangesRequest>(arguments, tool), ct),
        _ => throw WorkException.Invalid($"There is no work tool '{tool}'. The work tools are: {string.Join(", ", WorkTools.All.Select(definition => definition.Name))}.")
    };

    // ---- create_work -------------------------------------------------------------------------

    private async Task<object> CreateAsync(JsonElement raw, CreateWorkRequest request, WorkActor actor, CancellationToken ct)
    {
        var chosen = new[]
        {
            ("new_project", request.NewProject is not null),
            ("feature", request.Feature is not null),
            ("item", request.Item is not null),
            ("plan", request.Plan is not null)
        }.Where(entry => entry.Item2).Select(entry => entry.Item1).ToList();
        if (chosen.Count != 1)
            throw WorkException.Invalid($"create_work takes exactly one of new_project, feature, item, plan; this call passes {(chosen.Count == 0 ? "none" : string.Join(" and ", chosen))}. Nothing was created.");

        var keyHash = request.IdempotencyKey is { } key ? HashKey(actor, key) : null;
        if (request.NewProject is { } newProject)
        {
            if (request.Project is not null)
                throw WorkException.Invalid("create_work with new_project takes no project argument: the new project is the scope. Nothing was created.");
            var created = await Store.CreateProjectAsync(ReadNewProject(newProject), actor, keyHash, ct);
            return new CreateWorkResult(created.Created, Project: created.Value);
        }

        var project = await RequireProjectAsync(request.Project, "project", ct);
        if (request.Feature is { } feature)
        {
            var created = await Store.CreateFeatureAsync(project.Id, ReadFeature(feature, "feature"), actor, keyHash, ct);
            return new CreateWorkResult(created.Created, Feature: created.Value);
        }

        if (request.Item is { } item)
        {
            var created = await Store.CreateItemAsync(project.Id, await ReadItemAsync(item, project, ct), actor, keyHash, ct);
            return new CreateWorkResult(created.Created, Item: created.Value);
        }

        if (keyHash is null)
            throw WorkException.Invalid("create_work with plan needs idempotency_key, so that a retried import never creates the plan twice. Nothing was created.");

        // The size of the plan as the caller sent it.
        var bytes = Encoding.UTF8.GetByteCount(raw.GetRawText());
        if (bytes > WorkLimits.MaxPlanBytes)
            throw WorkException.Invalid($"The plan is {bytes} bytes of JSON; the largest plan is {WorkLimits.MaxPlanBytes} bytes. Nothing was created; import a smaller plan.");

        return await Store.ImportPlanAsync(project.Id, ReadPlan(request.Plan!), actor, keyHash, ct);
    }

    private static NewProjectSpec ReadNewProject(NewProjectInput input)
    {
        var key = input.Key.Trim().ToUpperInvariant();
        if (!WorkKeys.IsValidProjectKey(key))
            throw WorkException.Invalid($"new_project.key '{input.Key}' must be {WorkKeys.ProjectKeyRule}. Nothing was created.");

        return new NewProjectSpec(
            key,
            Text(input.Name, "new_project.name", WorkLimits.MaxProjectNameLength),
            OptionalText(input.Description, "new_project.description"),
            ReadGoals(input.Goals ?? [], "new_project.goals"),
            ReadRepositories(input.Repositories ?? [], "new_project.repositories"));
    }

    private static NewFeatureSpec ReadFeature(NewFeatureInput input, string where) =>
        new(Text(input.Title, $"{where}.title", WorkLimits.MaxTitleLength),
            OptionalText(input.Description, $"{where}.description"),
            ReadDocuments(input.Documents ?? [], $"{where}.documents"));

    private static IReadOnlyList<DocumentSpec> ReadDocuments(IReadOnlyList<DocumentInput> documents, string where) =>
        documents.Select((document, index) => new DocumentSpec(
            Text(document.Title, $"{where}[{index}].title", WorkLimits.MaxDocumentTitleLength),
            NonEmpty(document.Body, $"{where}[{index}].body"))).ToList();

    private async Task<NewItemSpec> ReadItemAsync(NewItemInput input, WorkTarget project, CancellationToken ct)
    {
        var featureId = input.Feature is { } feature ? (await RequireOfProjectAsync(feature, WorkEntityKind.Feature, project, "item.feature", ct)).Id : (Guid?)null;
        var blockers = new List<Guid>();
        foreach (var blocker in input.BlockedBy ?? [])
            blockers.Add((await RequireOfProjectAsync(blocker, WorkEntityKind.Item, project, "item.blocked_by", ct)).Id);

        return new NewItemSpec(
            Text(input.Title, "item.title", WorkLimits.MaxTitleLength),
            input.Type ?? WorkItemType.Story,
            input.Status ?? WorkStatus.Backlog,
            input.Priority ?? WorkPriority.Medium,
            OptionalText(input.Description, "item.description"),
            ReadLabels(input.Labels ?? [], "item.labels"),
            OptionalName(input.Repository, "item.repository"),
            featureId,
            blockers,
            input.Order ?? 0);
    }

    /// <summary>
    /// A plan as the rows an import stores, or the refusal: only its structure is checked, and its
    /// dependencies can only name each other (every item is new), so the cycle check runs on the
    /// plan's own edges, one at a time, as an add checks an edge against the stored ones.
    /// </summary>
    private static PlanSpec ReadPlan(PlanInput plan)
    {
        var features = plan.Features ?? [];
        if (features.Count > WorkLimits.MaxPlanFeatures)
            throw WorkException.Invalid($"A plan holds at most {WorkLimits.MaxPlanFeatures} features; this one holds {features.Count}. Nothing was created.");
        if (plan.Items.Count is 0 or > WorkLimits.MaxPlanItems)
            throw WorkException.Invalid($"plan.items must hold 1 to {WorkLimits.MaxPlanItems} items; it holds {plan.Items.Count}. Nothing was created.");

        var documentCount = features.Sum(feature => feature.Documents?.Count ?? 0);
        if (documentCount > WorkLimits.MaxPlanDocuments)
            throw WorkException.Invalid($"A plan holds at most {WorkLimits.MaxPlanDocuments} documents; this one holds {documentCount}. Nothing was created.");
        var dependencyCount = plan.Items.Sum(item => item.BlockedBy?.Count ?? 0);
        if (dependencyCount > WorkLimits.MaxPlanDependencies)
            throw WorkException.Invalid($"A plan holds at most {WorkLimits.MaxPlanDependencies} dependencies (blocked_by entries); this one holds {dependencyCount}. Nothing was created.");

        var featureIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var featureSpecs = new List<PlanFeatureSpec>();
        foreach (var (feature, index) in features.Select((feature, index) => (feature, index)))
        {
            var where = $"plan.features[{index}]";
            var reference = Ref(feature.Ref, where);
            if (!featureIndex.TryAdd(reference, index))
                throw WorkException.Invalid($"{where}: ref '{reference}' is used by more than one feature of the plan. Nothing was created.");
            featureSpecs.Add(new PlanFeatureSpec(reference, ReadFeature(new NewFeatureInput { Title = feature.Title, Description = feature.Description, Documents = feature.Documents }, where)));
        }

        var itemIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (item, index) in plan.Items.Select((item, index) => (item, index)))
        {
            var reference = Ref(item.Ref, $"plan.items[{index}]");
            if (!itemIndex.TryAdd(reference, index))
                throw WorkException.Invalid($"plan.items[{index}]: ref '{reference}' is used by more than one item of the plan. Refs are compared exactly. Nothing was created.");
        }

        var blockersOf = plan.Items.Select(_ => new HashSet<int>()).ToList();
        var items = new List<PlanItemSpec>();
        foreach (var (item, index) in plan.Items.Select((item, index) => (item, index)))
        {
            var where = $"Item '{item.Ref}' (plan.items[{index}])";
            int? feature = null;
            if (item.Feature is { } featureRef)
            {
                if (!featureIndex.TryGetValue(featureRef, out var found))
                    throw WorkException.Invalid($"{where}: feature names '{featureRef}', which is not the ref of a feature of the plan. Nothing was created.");
                feature = found;
            }

            foreach (var blockerRef in item.BlockedBy ?? [])
            {
                if (!itemIndex.TryGetValue(blockerRef, out var blocker))
                    throw WorkException.Invalid($"{where}: blocked_by names '{blockerRef}', which is not the ref of an item of the plan. Nothing was created.");
                if (blocker == index || WorkRules.IsBlockedBy(blockersOf, blocker, index))
                    throw new WorkException(WorkErrorCodes.Cycle,
                        $"Item '{item.Ref}' blocked by '{blockerRef}' would make an item of the plan block itself, directly or through other items. Nothing was created.");
                blockersOf[index].Add(blocker);
            }

            items.Add(new PlanItemSpec(
                item.Ref,
                Text(item.Title, $"{where}.title", WorkLimits.MaxTitleLength),
                item.Type ?? WorkItemType.Story,
                item.Status ?? WorkStatus.Backlog,
                item.Priority ?? WorkPriority.Medium,
                OptionalText(item.Description, $"{where}.description"),
                ReadLabels(item.Labels ?? [], $"{where}.labels"),
                OptionalName(item.Repository, $"{where}.repository"),
                feature,
                blockersOf[index].ToList(),
                item.Order ?? 0));
        }

        return new PlanSpec(featureSpecs, items);
    }

    // ---- get_work / find_work ----------------------------------------------------------------

    private async Task<object> GetAsync(GetWorkRequest request, CancellationToken ct)
    {
        var target = await RequireAsync(request.Id, "id", ct);
        return target.Kind switch
        {
            WorkEntityKind.Project => await Store.GetProjectAsync(target.Id, ct),
            WorkEntityKind.Feature => await Store.GetFeatureAsync(target.Id, ct),
            _ => (object?)await Store.GetItemAsync(target.Id, ct)
        } ?? throw WorkException.NotFound($"There is no project, feature or item '{request.Id}'.");
    }

    private async Task<object> FindAsync(FindWorkRequest request, CancellationToken ct)
    {
        var kind = request.Kind ?? WorkEntityKind.Item;
        var limit = request.Limit ?? WorkLimits.DefaultPageSize;
        if (limit is < 1 or > WorkLimits.MaxPageSize)
            throw WorkException.Invalid($"limit must be 1 to {WorkLimits.MaxPageSize}.");

        var itemOnly = new[]
        {
            ("status", request.Status is not null), ("type", request.Type is not null), ("priority", request.Priority is not null),
            ("repository", request.Repository is not null), ("feature", request.Feature is not null), ("label", request.Label is not null),
            ("ready", request.Ready is not null), ("claimed", request.Claimed is not null)
        }.Where(entry => entry.Item2).Select(entry => entry.Item1).ToList();
        if (kind != WorkEntityKind.Item && itemOnly.Count > 0)
            throw WorkException.Invalid($"find_work for {WorkJson.Name(kind)} rows takes no {string.Join(", ", itemOnly)}: those filter items. Its arguments are kind, project, text, detail, limit, cursor.");

        WorkTarget? project = null;
        if (kind == WorkEntityKind.Project)
        {
            if (request.Project is not null)
                throw WorkException.Invalid("find_work for projects takes no project argument.");
        }
        else
        {
            project = await RequireProjectAsync(request.Project, "project", ct);
        }

        Guid? featureId = request.Feature is { } feature ? (await RequireOfProjectAsync(feature, WorkEntityKind.Feature, project!, "feature", ct)).Id : null;
        var after = request.Cursor is { } cursor ? DecodeFindCursor(cursor, kind) : null;
        var words = (request.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 10)
            throw WorkException.Invalid("text holds at most 10 words.");

        var result = await Store.FindAsync(new FindWorkSpec(
            kind, project?.Id, request.Status, request.Type, request.Priority, OptionalName(request.Repository, "repository"), featureId,
            request.Label?.Trim(), request.Ready, request.Claimed, words, request.Detail ?? WorkDetail.Summary, limit, after), ct);
        return result with { NextCursor = result.NextCursor is { } next ? EncodeCursor(FindCursorKind, $"{WorkJson.Name(kind)}.{next}") : null };
    }

    private static string DecodeFindCursor(string cursor, WorkEntityKind kind)
    {
        var value = DecodeCursor(cursor, FindCursorKind, "find_work");
        var prefix = WorkJson.Name(kind) + ".";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            throw WorkException.Invalid("cursor belongs to a find_work of another kind; pass it back with the same filters.");
        return value[prefix.Length..];
    }

    // ---- update_work -------------------------------------------------------------------------

    private static readonly string[] ProjectFields = ["name", "description", "goals", "repositories", "clear"];
    private static readonly string[] FeatureFields = ["title", "description", "add_documents", "update_documents", "clear"];
    private static readonly string[] ItemFields =
    [
        "title", "description", "type", "status", "priority", "labels", "repository", "feature", "order", "add_blocked_by", "remove_blocked_by",
        "add_links", "remove_links", "comment", "claim", "renew_claim", "release_claim", "clear"
    ];

    private async Task<object> UpdateAsync(UpdateWorkRequest request, WorkActor actor, CancellationToken ct)
    {
        var target = await RequireAsync(request.Id, "id", ct);
        // The fields this call sets: everything it passed with a value, besides the address.
        var passed = JsonSerializer.SerializeToNode(request, WorkJson.Options)!.AsObject()
            .Select(property => property.Key)
            .Where(name => name is not ("id" or "expected_version"))
            .ToList();
        var (valid, clearable) = target.Kind switch
        {
            WorkEntityKind.Project => (ProjectFields, new[] { "description" }),
            WorkEntityKind.Feature => (FeatureFields, new[] { "description" }),
            _ => (ItemFields, new[] { "description", "labels", "repository", "feature" })
        };
        var kindName = WorkJson.Name(target.Kind);
        var foreign = passed.Where(name => !valid.Contains(name)).ToList();
        if (foreign.Count > 0)
            throw WorkException.Invalid($"update_work on a {kindName} takes no {string.Join(", ", foreign)}. Fields of a {kindName}: {string.Join(", ", valid)} (with id and expected_version). Nothing was changed.");
        if (passed.Count == 0)
            throw WorkException.Invalid($"update_work passes nothing to change. Fields of a {kindName}: {string.Join(", ", valid)}.");

        var clear = (request.Clear ?? []).Select(name => name.Trim()).ToList();
        var unknownClear = clear.Where(name => !clearable.Contains(name)).ToList();
        if (unknownClear.Count > 0)
            throw WorkException.Invalid($"clear on a {kindName} can name {string.Join(", ", clearable)}; not {string.Join(", ", unknownClear)}. Nothing was changed.");
        var both = clear.Where(passed.Contains).ToList();
        if (both.Count > 0)
            throw WorkException.Invalid($"{string.Join(", ", both)} is both passed and named in clear. Nothing was changed.");

        switch (target.Kind)
        {
            case WorkEntityKind.Project:
                return new UpdateWorkResult(Project: await Store.UpdateProjectAsync(target.Id, request.ExpectedVersion, new ProjectPatchSpec(
                    request.Name is { } name ? Text(name, "name", WorkLimits.MaxProjectNameLength) : null,
                    OptionalText(request.Description, "description"),
                    clear.Contains("description"),
                    request.Goals is { } goals ? ReadGoals(goals, "goals") : null,
                    request.Repositories is { } repositories ? ReadRepositories(repositories, "repositories") : null), actor, ct));

            case WorkEntityKind.Feature:
                return new UpdateWorkResult(Feature: await Store.UpdateFeatureAsync(target.Id, request.ExpectedVersion, new FeaturePatchSpec(
                    request.Title is { } title ? Text(title, "title", WorkLimits.MaxTitleLength) : null,
                    OptionalText(request.Description, "description"),
                    clear.Contains("description"),
                    request.AddDocuments is { } add ? ReadDocuments(add, "add_documents") : null,
                    request.UpdateDocuments?.Select((document, index) => ReadDocumentPatch(document, $"update_documents[{index}]")).ToList()), actor, ct));

            default:
                return new UpdateWorkResult(Item: await Store.UpdateItemAsync(target.Id, request.ExpectedVersion, await ReadItemPatchAsync(request, target, clear, ct), actor, ct));
        }
    }

    private static DocumentPatchSpec ReadDocumentPatch(DocumentPatchInput input, string where)
    {
        if (!Guid.TryParse(input.Id, out var id))
            throw WorkException.Invalid($"{where}.id must be the id of a document of the feature.");
        if (input.Title is null && input.Body is null)
            throw WorkException.Invalid($"{where} changes nothing: pass title, body or both.");
        return new DocumentPatchSpec(
            id,
            input.Title is { } title ? Text(title, $"{where}.title", WorkLimits.MaxDocumentTitleLength) : null,
            input.Body is { } body ? NonEmpty(body, $"{where}.body") : null);
    }

    private async Task<ItemPatchSpec> ReadItemPatchAsync(UpdateWorkRequest request, WorkTarget item, IReadOnlyList<string> clear, CancellationToken ct)
    {
        var project = new WorkTarget(WorkEntityKind.Project, item.ProjectId, item.ProjectId);
        var claims = new[] { ("claim", request.Claim is not null), ("renew_claim", request.RenewClaim is not null), ("release_claim", request.ReleaseClaim is not null) }
            .Where(entry => entry.Item2).Select(entry => entry.Item1).ToList();
        if (claims.Count > 1)
            throw WorkException.Invalid($"Pass at most one of claim, renew_claim, release_claim; this call passes {string.Join(" and ", claims)}. Nothing was changed.");

        ClaimSpec? claim = request.Claim is { } take ? new ClaimSpec(ClaimAction.Claim, Marker(take.Marker, "claim.marker"), Ttl(take.TtlSeconds, "claim.ttl_seconds"))
            : request.RenewClaim is { } renew ? new ClaimSpec(ClaimAction.Renew, Marker(renew.Marker, "renew_claim.marker"), Ttl(renew.TtlSeconds, "renew_claim.ttl_seconds"))
            : request.ReleaseClaim is { } release ? new ClaimSpec(ClaimAction.Release, Marker(release.Marker, "release_claim.marker"), 0)
            : null;

        var add = new List<Guid>();
        foreach (var blocker in request.AddBlockedBy ?? [])
            add.Add((await RequireOfProjectAsync(blocker, WorkEntityKind.Item, project, "add_blocked_by", ct)).Id);
        var remove = new List<Guid>();
        foreach (var blocker in request.RemoveBlockedBy ?? [])
            remove.Add((await RequireOfProjectAsync(blocker, WorkEntityKind.Item, project, "remove_blocked_by", ct)).Id);

        Guid? featureId = request.Feature is { } feature ? (await RequireOfProjectAsync(feature, WorkEntityKind.Feature, project, "feature", ct)).Id : null;

        return new ItemPatchSpec(
            Title: request.Title is { } title ? Text(title, "title", WorkLimits.MaxTitleLength) : null,
            Description: OptionalText(request.Description, "description"),
            ClearDescription: clear.Contains("description"),
            Type: request.Type,
            Status: request.Status,
            Priority: request.Priority,
            Labels: request.Labels is { } labels ? ReadLabels(labels, "labels", allowEmpty: false) : null,
            ClearLabels: clear.Contains("labels"),
            Repository: OptionalName(request.Repository, "repository"),
            ClearRepository: clear.Contains("repository"),
            FeatureId: featureId,
            ClearFeature: clear.Contains("feature"),
            Order: request.Order,
            AddBlockedBy: add,
            RemoveBlockedBy: remove,
            AddLinks: request.AddLinks?.Select((link, index) => ReadLink(link, $"add_links[{index}]")).ToList(),
            RemoveLinks: request.RemoveLinks?.Select((link, index) => ReadLink(link, $"remove_links[{index}]")).ToList(),
            Comment: request.Comment is { } comment ? NonEmpty(comment, "comment") : null,
            Claim: claim);
    }

    // ---- next_work ---------------------------------------------------------------------------

    private async Task<object> NextAsync(NextWorkRequest request, WorkActor actor, CancellationToken ct)
    {
        var project = await RequireProjectAsync(request.Project, "project", ct);
        return await Store.NextWorkAsync(new NextWorkSpec(
            project.Id,
            Marker(request.Marker, "marker"),
            Ttl(request.TtlSeconds, "ttl_seconds"),
            request.Another ?? false,
            request.IdempotencyKey is { } key ? HashKey(actor, key) : null,
            OptionalName(request.Repository, "repository"),
            request.Type,
            request.Label?.Trim()), actor, ct);
    }

    // ---- changes -----------------------------------------------------------------------------

    private async Task<object> ChangesAsync(ChangesRequest request, CancellationToken ct)
    {
        var limit = request.Limit ?? WorkLimits.DefaultPageSize;
        if (limit is < 1 or > WorkLimits.MaxPageSize)
            throw WorkException.Invalid($"limit must be 1 to {WorkLimits.MaxPageSize}.");
        var wait = request.WaitSeconds ?? 0;
        if (wait is < 0 or > WorkLimits.MaxWaitSeconds)
            throw WorkException.Invalid($"wait_seconds must be 0 to {WorkLimits.MaxWaitSeconds}.");
        if (request.Cursor is not null && request.FromStart == true)
            throw WorkException.Invalid("Pass cursor or from_start, not both.");

        Guid? projectId = request.Project is { } project ? (await RequireProjectAsync(project, "project", ct)).Id : null;

        long after;
        if (request.Cursor is { } cursor)
        {
            var value = DecodeCursor(cursor, ChangeCursorKind, "changes");
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out after))
                throw WorkException.Invalid("cursor is not a cursor of changes; pass next_cursor back unchanged.");
            if (after > await Store.GetChangeHeadAsync(ct))
                throw WorkException.Invalid("cursor is ahead of the newest entry of the feed (the database was restored to an earlier state). Start again without cursor.");
        }
        else if (request.FromStart == true)
        {
            after = 0;
        }
        else
        {
            // Where to start following from: the newest entry, and none of the entries before it.
            return new ChangesResult([], EncodeCursor(ChangeCursorKind, (await Store.GetChangeHeadAsync(ct)).ToString(CultureInfo.InvariantCulture)));
        }

        var deadline = clock.GetUtcNow().AddSeconds(wait);
        while (true)
        {
            var entries = await Store.ReadChangesAsync(projectId, after, limit, ct);
            if (entries.Count > 0 || clock.GetUtcNow() >= deadline)
            {
                var next = entries.Count > 0 ? entries[^1].Sequence : after;
                return new ChangesResult(entries, EncodeCursor(ChangeCursorKind, next.ToString(CultureInfo.InvariantCulture)));
            }

            await Task.Delay(PollInterval, clock, ct);
        }
    }

    // ---- Resolution --------------------------------------------------------------------------

    private async Task<WorkTarget> RequireAsync(string idOrKey, string argument, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idOrKey))
            throw WorkException.Invalid($"{argument} must be a key (SW, SW-142, SW-F12) or an id.");
        return await Store.ResolveAsync(idOrKey, ct)
            ?? throw WorkException.NotFound($"There is no project, feature or item '{idOrKey.Trim()}'.");
    }

    private async Task<WorkTarget> RequireProjectAsync(string? idOrKey, string argument, CancellationToken ct)
    {
        if (idOrKey is null)
            throw WorkException.Invalid($"{argument} is required: the key (such as SW) or id of a Project.");
        var target = await RequireAsync(idOrKey, argument, ct);
        if (target.Kind != WorkEntityKind.Project)
            throw WorkException.Invalid($"{argument} '{idOrKey}' is a {WorkJson.Name(target.Kind)}, not a project.");
        return target;
    }

    private async Task<WorkTarget> RequireOfProjectAsync(string idOrKey, WorkEntityKind kind, WorkTarget project, string argument, CancellationToken ct)
    {
        var target = await RequireAsync(idOrKey, argument, ct);
        if (target.Kind != kind)
            throw WorkException.Invalid($"{argument} '{idOrKey}' is a {WorkJson.Name(target.Kind)}, not a{(kind == WorkEntityKind.Item ? "n" : string.Empty)} {WorkJson.Name(kind)}.");
        if (target.ProjectId != project.Id)
            throw WorkException.Invalid($"{argument} '{idOrKey}' belongs to another project: work stays within its project.");
        return target;
    }

    // ---- Field rules -------------------------------------------------------------------------

    private static string HashKey(WorkActor actor, string key)
    {
        if (key.Length is 0 or > WorkLimits.MaxIdempotencyKeyLength || key.Any(character => character is <= ' ' or > '~'))
            throw WorkException.Invalid($"idempotency_key must be 1 to {WorkLimits.MaxIdempotencyKeyLength} printable ASCII characters without spaces.");
        return WorkRules.HashIdempotencyKey(actor.Name, key);
    }

    private static string Text(string value, string argument, int maxLength)
    {
        var text = value.Trim();
        if (text.Length == 0)
            throw WorkException.Invalid($"{argument} must not be empty.");
        if (text.Length > maxLength)
            throw WorkException.Invalid($"{argument} must be at most {maxLength} characters.");
        return text;
    }

    private static string NonEmpty(string value, string argument) =>
        string.IsNullOrWhiteSpace(value) ? throw WorkException.Invalid($"{argument} must not be empty.") : value;

    // An empty string is not a way to clear: name the field in clear.
    private static string? OptionalText(string? value, string argument) =>
        value is null ? null
        : string.IsNullOrWhiteSpace(value) ? throw WorkException.Invalid($"{argument} must not be empty; to empty it, name it in clear (update_work) or leave it out.")
        : value.Trim();

    private static string? OptionalName(string? value, string argument) =>
        value is null ? null
        : string.IsNullOrWhiteSpace(value) || value.Trim().Length > 255 ? throw WorkException.Invalid($"{argument} must be a repository name of 1 to 255 characters.")
        : value.Trim();

    private static string Ref(string value, string where)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > WorkLimits.MaxRefLength || value.Any(char.IsControl))
            throw WorkException.Invalid($"{where}.ref must be 1 to {WorkLimits.MaxRefLength} characters, without control characters.");
        return value;
    }

    private static string Marker(string value, string argument)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > WorkLimits.MaxMarkerLength || value.Any(char.IsControl))
            throw WorkException.Invalid($"{argument} must be 1 to {WorkLimits.MaxMarkerLength} characters without control characters.");
        return value;
    }

    private static int Ttl(int? value, string argument)
    {
        var ttl = value ?? WorkLimits.DefaultClaimTtlSeconds;
        if (ttl is < 1 or > WorkLimits.MaxClaimTtlSeconds)
            throw WorkException.Invalid($"{argument} must be 1 to {WorkLimits.MaxClaimTtlSeconds}.");
        return ttl;
    }

    private static IReadOnlyList<string> ReadGoals(IReadOnlyList<string> goals, string argument)
    {
        if (goals.Count > WorkLimits.MaxGoals)
            throw WorkException.Invalid($"{argument} holds at most {WorkLimits.MaxGoals} goals.");
        return goals.Select((goal, index) => Text(goal, $"{argument}[{index}]", WorkLimits.MaxGoalLength)).ToList();
    }

    private static IReadOnlyList<RepositoryMemberView> ReadRepositories(IReadOnlyList<RepositoryMemberInput> repositories, string argument)
    {
        if (repositories.Count > WorkLimits.MaxRepositories)
            throw WorkException.Invalid($"{argument} holds at most {WorkLimits.MaxRepositories} repositories.");
        var members = repositories.Select((member, index) => new RepositoryMemberView(OptionalName(member.Repository, $"{argument}[{index}].repository")!, member.Role)).ToList();
        var duplicate = members.GroupBy(member => member.Repository, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw WorkException.Invalid($"{argument} names '{duplicate.Key}' more than once.");
        return members;
    }

    /// <summary>Labels as stored: trimmed, without duplicates (ignoring case), each without a comma.</summary>
    private static IReadOnlyList<string> ReadLabels(IReadOnlyList<string> labels, string argument, bool allowEmpty = true)
    {
        if (labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Contains(',')))
            throw WorkException.Invalid($"Every label of {argument} must be a non-empty string without a comma.");
        if (!allowEmpty && labels.Count == 0)
            throw WorkException.Invalid($"{argument} must not be empty; to remove every label, name labels in clear.");
        var distinct = labels.Select(label => label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (string.Join(',', distinct).Length > WorkLimits.MaxLabelsLength)
            throw WorkException.Invalid($"The labels of an item, joined with commas, must be at most {WorkLimits.MaxLabelsLength} characters.");
        return distinct;
    }

    // An absolute http or https URL without whitespace, control characters or credentials.
    private static LinkSpec ReadLink(LinkInput link, string where)
    {
        var url = link.Url.Trim();
        var valid = url.Length is > 0 and <= WorkLimits.MaxLinkUrlLength &&
            !url.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) &&
            Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            parsed.Host.Length > 0 &&
            parsed.UserInfo.Length == 0;
        if (!valid)
            throw WorkException.Invalid($"{where}.url must be an absolute http or https URL of at most {WorkLimits.MaxLinkUrlLength} characters, without whitespace or credentials.");
        return new LinkSpec(link.Kind, url);
    }

    // ---- Cursors -----------------------------------------------------------------------------

    private static string EncodeCursor(string kind, string value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{kind}:{value}"));

    private static string DecodeCursor(string cursor, string kind, string tool)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor.Trim()));
            if (text.StartsWith(kind + ":", StringComparison.Ordinal))
                return text[(kind.Length + 1)..];
        }
        catch (FormatException)
        {
        }

        throw WorkException.Invalid($"cursor is not a cursor of {tool}; pass next_cursor back unchanged.");
    }
}
