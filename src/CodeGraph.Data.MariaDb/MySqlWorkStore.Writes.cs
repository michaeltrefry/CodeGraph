using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore
{
    private const string ProjectOperation = "project";
    private const string FeatureOperation = "feature";
    private const string ItemOperation = "item";
    private const string PlanOperation = "plan";
    private const string NextWorkOperation = "next_work";

    // ---- Projects ----------------------------------------------------------------------------

    public Task<WorkCreated<ProjectView>> CreateProjectAsync(NewProjectSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) =>
        // A project has no parent row to hold, so creates run one at a time under a named lock
        // (KanbanBoard's kanban_project_creates): the look-up of the key and the INSERT of two
        // concurrent creates cannot interleave, and the second finds the first one's project.
        RunAsync([ProjectCreateLockName], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (keyHash is not null && await FindKeyAsync(connection, transaction, GlobalScope, ProjectOperation, keyHash, ct) is { EntityId: { } existing })
            {
                var current = await LoadProjectViewAsync(connection, transaction, existing, withCounts: false, ct);
                if (current is not null)
                    return new WorkCreated<ProjectView>(false, current);
            }

            var taken = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM work_projects WHERE project_key = @Key", new { spec.Key }, transaction, cancellationToken: ct));
            if (taken is not null)
                throw new WorkException(WorkErrorCodes.Conflict, $"Project key '{spec.Key}' is taken by another project. Nothing was created.");

            var repositories = await ValidateRepositoriesAsync(connection, transaction, null, spec.Repositories, ct);

            var id = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_projects (id, project_key, name, description, goals_json, version, created_by, created_by_kind, created_session_id, created_at, updated_at) " +
                "VALUES (@id, @Key, @Name, @Description, @goals, 1, @actorName, @actorKind, @actorSession, @now, @now)",
                new { id, spec.Key, spec.Name, spec.Description, goals = JsonSerializer.Serialize(spec.Goals), actorName = actor.Name, actorKind = actor.Kind, actorSession = actor.SessionId, now },
                transaction, cancellationToken: ct));
            await WriteRepositoriesAsync(connection, transaction, id, repositories, ct);
            if (keyHash is not null)
                await InsertKeyAsync(connection, transaction, GlobalScope, ProjectOperation, keyHash, id, null, now, ct);

            var view = (await LoadProjectViewAsync(connection, transaction, id, withCounts: false, ct))!;
            await CommitAsync(connection, transaction,
                [new PendingChange(id, view.Key, WorkEntityKind.Project, id, view.Key, WorkChangeKinds.ProjectCreated, Diff(null, Snapshot(view)))],
                actor, now, ct);
            return new WorkCreated<ProjectView>(true, view);
        }, ct);

    private Task<ProjectView> UpdateProjectCoreAsync(Guid projectId, long expectedVersion, ProjectPatchSpec patch, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // The conditional UPDATE is the first statement: it decides expected_version and holds
            // the project's row, which every create in the project and every item write that names
            // a repository also takes first.
            await AdvanceAsync(connection, transaction, "work_projects", projectId, expectedVersion, now,
                () => LoadProjectViewAsync(connection, transaction, projectId, withCounts: false, ct), "Project", ct);

            var before = (await LoadProjectViewAsync(connection, transaction, projectId, withCounts: false, ct))!;
            var name = patch.Name ?? before.Name;
            var description = patch.ClearDescription ? null : patch.Description ?? before.Description;
            var goals = patch.Goals ?? before.Goals;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_projects SET name = @name, description = @description, goals_json = @goals WHERE id = @projectId",
                new { name, description, goals = JsonSerializer.Serialize(goals), projectId }, transaction, cancellationToken: ct));

            if (patch.Repositories is { } requested)
            {
                var repositories = await ValidateRepositoriesAsync(connection, transaction, projectId, requested, ct);
                var keptOwned = repositories.Where(member => member.Role == RepositoryRole.Owned).Select(member => member.Repository).ToHashSet(StringComparer.Ordinal);
                var dropped = before.Repositories
                    .Where(member => member.Role == RepositoryRole.Owned && !keptOwned.Contains(member.Repository))
                    .Select(member => member.Repository)
                    .ToArray();
                if (dropped.Length > 0)
                {
                    var inUse = (await connection.QueryAsync<string>(new CommandDefinition(
                        "SELECT DISTINCT repository_name FROM work_items WHERE project_id = @projectId AND repository_name IN @dropped",
                        new { projectId, dropped }, transaction, cancellationToken: ct))).ToList();
                    if (inUse.Count > 0)
                        throw new WorkException(WorkErrorCodes.Conflict,
                            $"Items of this project still name {string.Join(", ", inUse)} as their repository, so it cannot stop being an owned repository. Move or clear those items first. Nothing was changed.");
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM work_project_repositories WHERE project_id = @projectId", new { projectId }, transaction, cancellationToken: ct));
                await WriteRepositoriesAsync(connection, transaction, projectId, repositories, ct);
            }

            var after = (await LoadProjectViewAsync(connection, transaction, projectId, withCounts: false, ct))!;
            var beforeSnapshot = Snapshot(before);
            beforeSnapshot["version"] = expectedVersion;
            var projectChanges = Diff(beforeSnapshot, Snapshot(after));
            // The save-point fields (S5): in this transaction, project-wide unless they name repositories.
            if (patch.Knowledge is { Count: > 0 } knowledge)
            {
                await InsertKnowledgeAsync(connection, transaction, (await ReadProjectRowAsync(connection, transaction, projectId, ct))!, knowledge, null, null, null, actor, now, ct);
                projectChanges.Add(Change("knowledge", null, knowledge.Select(entry => entry.Id.ToString()).ToList()));
            }

            await CommitAsync(connection, transaction,
                [new PendingChange(projectId, after.Key, WorkEntityKind.Project, projectId, after.Key, WorkChangeKinds.ProjectUpdated, projectChanges)],
                actor, now, ct);
            return after;
        }, ct);

    private static Dictionary<string, object?> Snapshot(ProjectView project) => new()
    {
        ["key"] = project.Key,
        ["name"] = project.Name,
        ["description"] = project.Description,
        ["goals"] = project.Goals,
        ["repositories"] = project.Repositories,
        ["version"] = project.Version
    };

    // Every repository is one CodeGraph indexes, and an owned one is owned by no other project. A
    // name is matched without regard to case and stored as CodeGraph spells it (repositories.name);
    // the members come back with that canonical spelling.
    private static async Task<IReadOnlyList<RepositoryMemberView>> ValidateRepositoriesAsync(MySqlConnection connection, MySqlTransaction transaction, Guid? projectId, IReadOnlyList<RepositoryMemberView> repositories, CancellationToken ct)
    {
        if (repositories.Count == 0)
            return repositories;

        var names = repositories.Select(member => member.Repository).ToArray();
        var known = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM repositories WHERE name IN @names", new { names }, transaction, cancellationToken: ct)))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var unknown = names.Where(name => !known.ContainsKey(name)).ToList();
        if (unknown.Count > 0)
            throw WorkException.Invalid($"CodeGraph indexes no repository named {string.Join(", ", unknown.Select(name => $"'{name}'"))}. Nothing was changed.");

        repositories = repositories.Select(member => member with { Repository = known[member.Repository] }).ToList();
        var twice = repositories.GroupBy(member => member.Repository, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (twice.Count > 0)
            throw WorkException.Invalid($"A repository is listed once: {string.Join(", ", twice)} appears more than once. Nothing was changed.");

        var owned = repositories.Where(member => member.Role == RepositoryRole.Owned).Select(member => member.Repository).ToArray();
        if (owned.Length == 0)
            return repositories;

        var elsewhere = (await connection.QueryAsync<(string RepositoryName, string ProjectKey)>(new CommandDefinition(
            "SELECT r.repository_name, p.project_key FROM work_project_repositories r JOIN work_projects p ON p.id = r.project_id " +
            "WHERE r.owned_repository IN @owned AND (@projectId IS NULL OR r.project_id <> @projectId)",
            new { owned, projectId }, transaction, cancellationToken: ct))).ToList();
        if (elsewhere.Count > 0)
            throw new WorkException(WorkErrorCodes.Conflict,
                $"A repository is owned by at most one project: {string.Join(", ", elsewhere.Select(hit => $"{hit.RepositoryName} is owned by {hit.ProjectKey}"))}. Make it a dependency here, or change the other project first. Nothing was changed.");
        return repositories;
    }

    private static async Task WriteRepositoriesAsync(MySqlConnection connection, MySqlTransaction transaction, Guid projectId, IReadOnlyList<RepositoryMemberView> repositories, CancellationToken ct)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_project_repositories (project_id, repository_name, role, position) VALUES (@projectId, @Repository, @role, @position)",
                repositories.Select((member, position) => new { projectId, member.Repository, role = JsonName(member.Role), position }),
                transaction, cancellationToken: ct));
        }
        catch (MySqlException exception) when (exception.Number == MySqlDuplicateEntry && exception.Message.Contains("ux_work_project_repositories_owned", StringComparison.Ordinal))
        {
            // Another project took the repository as owned while this write ran.
            throw new WorkException(WorkErrorCodes.Conflict, "A repository of this write was made owned by another project at the same time. Nothing was changed.");
        }
    }

    // ---- Features ----------------------------------------------------------------------------

    public Task<WorkCreated<FeatureView>> CreateFeatureAsync(Guid projectId, NewFeatureSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var project = await HoldProjectAsync(connection, transaction, projectId, ct);
            if (keyHash is not null && await FindKeyAsync(connection, transaction, projectId, FeatureOperation, keyHash, ct) is { EntityId: { } existing })
                return new WorkCreated<FeatureView>(false, (await LoadFeatureViewAsync(connection, transaction, existing, withCounts: false, ct))!);

            var id = await InsertFeatureAsync(connection, transaction, project, project.NextFeatureNumber, spec, actor, now, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_projects SET next_feature_number = next_feature_number + 1 WHERE id = @projectId", new { projectId }, transaction, cancellationToken: ct));
            if (keyHash is not null)
                await InsertKeyAsync(connection, transaction, projectId, FeatureOperation, keyHash, id, null, now, ct);

            var view = (await LoadFeatureViewAsync(connection, transaction, id, withCounts: false, ct))!;
            await CommitAsync(connection, transaction, [FeatureCreatedEntry(view)], actor, now, ct);
            return new WorkCreated<FeatureView>(true, view);
        }, ct);

    private static async Task<Guid> InsertFeatureAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, int number, NewFeatureSpec spec, WorkActor actor, DateTime now, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var requirements = MergeRequirements([], spec.Plan?.Requirements, null, WorkKeys.Feature(project.ProjectKey, number));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_features (id, project_id, number, title, description, goals_json, requirements_json, acceptance_tests_json, version, created_by, created_by_kind, created_session_id, created_at, updated_at) " +
            "VALUES (@id, @projectId, @number, @Title, @Description, @goals, @requirements, @tests, 1, @actorName, @actorKind, @actorSession, @now, @now)",
            new
            {
                id, projectId = project.Id, number, spec.Title, spec.Description, goals = WriteList(spec.Plan?.Goals), requirements = WriteList(requirements),
                tests = WriteList(spec.Plan?.AcceptanceTests), actorName = actor.Name, actorKind = actor.Kind, actorSession = actor.SessionId, now
            },
            transaction, cancellationToken: ct));
        await InsertDocumentsAsync(connection, transaction, id, 0, spec.Documents, actor, now, ct);
        return id;
    }

    private static Task InsertDocumentsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid featureId, int firstPosition, IReadOnlyList<DocumentSpec> documents, WorkActor actor, DateTime now, CancellationToken ct) =>
        documents.Count == 0
            ? Task.CompletedTask
            : connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_feature_documents (id, feature_id, position, title, body, created_by, created_at, updated_at) " +
                "VALUES (@id, @featureId, @position, @Title, @Body, @createdBy, @now, @now)",
                documents.Select((document, index) => new { id = Guid.NewGuid(), featureId, position = firstPosition + index, document.Title, document.Body, createdBy = actor.Name, now }),
                transaction, cancellationToken: ct));

    private static PendingChange FeatureCreatedEntry(FeatureView feature) =>
        new(feature.ProjectId, feature.ProjectKey, WorkEntityKind.Feature, feature.Id, feature.Key, WorkChangeKinds.FeatureCreated,
            WithDocuments(Diff(null, Snapshot(feature)), feature.Documents, null));

    private static Dictionary<string, object?> Snapshot(FeatureView feature) => new()
    {
        ["title"] = feature.Title,
        ["description"] = feature.Description,
        ["goals"] = feature.Goals,
        ["requirements"] = feature.Requirements?.Select(requirement => requirement with { CoveredBy = null }).ToList(),
        ["acceptance_tests"] = feature.AcceptanceTests,
        ["version"] = feature.Version
    };

    // One "document" change per document added ({id, title, body}), or changed (old and new).
    private static JsonArray WithDocuments(JsonArray changes, IReadOnlyList<DocumentView> added, IReadOnlyList<(DocumentView Before, DocumentView After)>? changed)
    {
        foreach (var document in added)
            changes.Add(Change("document", null, new { document.Id, document.Title, document.Body }));
        foreach (var (before, after) in changed ?? [])
            changes.Add(Change("document", new { before.Id, before.Title, before.Body }, new { after.Id, after.Title, after.Body }));
        return changes;
    }

    private Task<FeatureView> UpdateFeatureCoreAsync(Guid featureId, long expectedVersion, FeaturePatchSpec patch, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // Save points (S5) reference the project: hold its row before the feature's (project, then knowledge).
            if (patch.Knowledge is { Count: > 0 } &&
                await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT project_id FROM work_features WHERE id = @featureId", new { featureId }, transaction, cancellationToken: ct)) is { } featureProject)
                await HoldProjectAsync(connection, transaction, featureProject, ct);
            await AdvanceAsync(connection, transaction, "work_features", featureId, expectedVersion, now,
                () => LoadFeatureViewAsync(connection, transaction, featureId, withCounts: false, ct), "Feature", ct);

            var before = (await LoadFeatureViewAsync(connection, transaction, featureId, withCounts: false, ct))!;
            var storedRequirements = (before.Requirements ?? []).Select(requirement => requirement with { CoveredBy = null }).ToList();
            var requirements = MergeRequirements(storedRequirements, patch.Requirements, patch.DropRequirements, before.Key);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_features SET title = @title, description = @description, goals_json = @goals, requirements_json = @requirements, " +
                "acceptance_tests_json = @tests WHERE id = @featureId",
                new
                {
                    title = patch.Title ?? before.Title,
                    description = patch.ClearDescription ? null : patch.Description ?? before.Description,
                    goals = patch.ClearGoals ? null : WriteList(patch.Goals ?? before.Goals),
                    requirements = WriteList(requirements),
                    tests = patch.ClearAcceptanceTests ? null : WriteList(patch.AcceptanceTests ?? before.AcceptanceTests),
                    featureId
                },
                transaction, cancellationToken: ct));

            var changed = new List<(DocumentView Before, DocumentView After)>();
            foreach (var documentPatch in patch.UpdateDocuments ?? [])
            {
                var document = before.Documents.FirstOrDefault(candidate => candidate.Id == documentPatch.Id)
                    ?? throw WorkException.NotFound($"Feature {before.Key} has no document {documentPatch.Id}. Nothing was changed.");
                var updated = document with { Title = documentPatch.Title ?? document.Title, Body = documentPatch.Body ?? document.Body, UpdatedAt = now };
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_feature_documents SET title = @Title, body = @Body, updated_at = @now WHERE id = @Id",
                    new { updated.Title, updated.Body, now, updated.Id }, transaction, cancellationToken: ct));
                changed.Add((document, updated));
            }

            var firstPosition = before.Documents.Count == 0 ? 0 : before.Documents.Max(document => document.Position) + 1;
            await InsertDocumentsAsync(connection, transaction, featureId, firstPosition, patch.AddDocuments ?? [], actor, now, ct);

            var after = (await LoadFeatureViewAsync(connection, transaction, featureId, withCounts: false, ct))!;
            var beforeSnapshot = Snapshot(before);
            beforeSnapshot["version"] = expectedVersion;
            var added = after.Documents.Where(document => before.Documents.All(existing => existing.Id != document.Id)).ToList();
            var featureChanges = WithDocuments(Diff(beforeSnapshot, Snapshot(after)), added, changed);
            // The save-point fields (S5): in this transaction, linked to the feature.
            if (patch.Knowledge is { Count: > 0 } knowledge)
            {
                var project = (await ReadProjectRowAsync(connection, transaction, after.ProjectId, ct))!;
                await InsertKnowledgeAsync(connection, transaction, project, knowledge, null, featureId, null, actor, now, ct);
                featureChanges.Add(Change("knowledge", null, knowledge.Select(entry => entry.Id.ToString()).ToList()));
            }

            await CommitAsync(connection, transaction,
                [new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Feature, after.Id, after.Key, WorkChangeKinds.FeatureUpdated, featureChanges)],
                actor, now, ct);
            return after;
        }, ct);

    // ---- Items -------------------------------------------------------------------------------

    public Task<WorkCreated<ItemView>> CreateItemAsync(Guid projectId, NewItemSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) =>
        // With or without a key the create starts by holding the project's row: creates into one
        // project run one at a time, so each takes the next number after the one before it has
        // committed (no gaps, no duplicates) and a keyed repeat finds the first one's item.
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var project = await HoldProjectAsync(connection, transaction, projectId, ct);
            if (keyHash is not null && await FindKeyAsync(connection, transaction, projectId, ItemOperation, keyHash, ct) is { EntityId: { } existing })
                return new WorkCreated<ItemView>(false, (await LoadItemViewAsync(connection, transaction, existing, ct))!);

            var itemRepository = spec.Repository is { } repository
                ? await RequireOwnedRepositoryAsync(connection, transaction, project, repository, ct)
                : null;
            if (spec.FeatureId is { } featureId)
                await RequireFeatureAsync(connection, transaction, project, featureId, ct);
            if (spec.BlockedBy.Count > 0)
                await RequireItemsOfProjectAsync(connection, transaction, project, spec.BlockedBy, ct);
            var plan = spec.Plan ?? new ItemPlanFields();
            if (plan.Inherits is { Count: > 0 } inherits)
                await RequireInheritsAsync(connection, transaction, spec.FeatureId, inherits, "item.inherits", ct);

            // A new item blocks nothing yet, so its blockers cannot close a cycle: no dependency lock.
            var id = Guid.NewGuid();
            await InsertItemAsync(connection, transaction, project.Id, id, project.NextItemNumber, spec.Title, spec.Type, spec.Status, spec.Priority,
                spec.Description, spec.Labels, itemRepository, spec.FeatureId, spec.Order, actor, now, ct, plan);
            await InsertEdgesAsync(connection, transaction, spec.BlockedBy.Distinct().Select(blocker => (id, blocker)), now, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_projects SET next_item_number = next_item_number + 1 WHERE id = @projectId", new { projectId }, transaction, cancellationToken: ct));
            if (keyHash is not null)
                await InsertKeyAsync(connection, transaction, projectId, ItemOperation, keyHash, id, null, now, ct);

            var view = (await LoadItemViewAsync(connection, transaction, id, ct))!;
            await CommitAsync(connection, transaction, [ItemCreatedEntry(view)], actor, now, ct);
            return new WorkCreated<ItemView>(true, view);
        }, ct);

    private static Task InsertItemAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid projectId, Guid id, int number, string title, WorkItemType type, WorkStatus status,
        WorkPriority priority, string? description, IReadOnlyList<string> labels, string? repository, Guid? featureId, int order, WorkActor actor, DateTime now, CancellationToken ct,
        ItemPlanFields? plan = null) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_items (id, project_id, number, feature_id, item_type, title, description, status, priority, sort_order, labels, repository_name, " +
            "plan_ref, goal, acceptance_json, inherits_json, touches_json, " +
            "version, created_by, created_by_kind, created_session_id, created_at, updated_at) " +
            "VALUES (@id, @projectId, @number, @featureId, @type, @title, @description, @status, @priority, @order, @labels, @repository, " +
            "@planRef, @goal, @acceptance, @inherits, @touches, 1, @actorName, @actorKind, @actorSession, @now, @now)",
            new
            {
                id, projectId, number, featureId, type = JsonName(type), title, description, status = WorkStatuses.Name(status), priority = JsonName(priority),
                order, labels = JoinLabels(labels), repository,
                planRef = featureId is null ? null : plan?.Ref, goal = plan?.Goal,
                acceptance = WriteList(NormalizeCriteria([], plan?.AcceptanceCriteria ?? [])), inherits = WriteList(plan?.Inherits), touches = WriteList(plan?.Touches),
                actorName = actor.Name, actorKind = actor.Kind, actorSession = actor.SessionId, now
            },
            transaction, cancellationToken: ct));

    private static Task InsertEdgesAsync(MySqlConnection connection, MySqlTransaction transaction, IEnumerable<(Guid Blocked, Guid Blocker)> edges, DateTime now, CancellationToken ct)
    {
        var rows = edges.Select(edge => new { blocked = edge.Blocked, blocker = edge.Blocker, now }).ToList();
        return rows.Count == 0
            ? Task.CompletedTask
            : connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_item_dependencies (blocked_item_id, blocker_item_id, created_at) VALUES (@blocked, @blocker, @now)",
                rows, transaction, cancellationToken: ct));
    }

    private static PendingChange ItemCreatedEntry(ItemView item)
    {
        var changes = Diff(null, Snapshot(item));
        foreach (var blocker in item.BlockedBy)
            changes.Add(Change("blocked_by", null, blocker.Key));
        return new PendingChange(item.ProjectId, item.ProjectKey, WorkEntityKind.Item, item.Id, item.Key, WorkChangeKinds.ItemCreated, changes);
    }

    // The fields of an item an update or a create can change, as the feed names them. The claim
    // is the stored one (a lapsed claim is still what is stored).
    private static Dictionary<string, object?> Snapshot(ItemView item) => new()
    {
        ["title"] = item.Title,
        ["description"] = item.Description,
        ["type"] = item.Type,
        ["status"] = item.Status,
        ["priority"] = item.Priority,
        ["order"] = item.Order,
        ["labels"] = item.Labels.Count == 0 ? null : item.Labels,
        ["repository"] = item.Repository,
        ["feature"] = item.Feature?.Key,
        ["ref"] = item.Ref,
        ["goal"] = item.Goal,
        ["acceptance_criteria"] = item.AcceptanceCriteria,
        ["inherits"] = item.Inherits,
        ["touches"] = item.Touches,
        ["merged_awaiting_acceptance"] = item.MergedAwaitingAcceptance,
        ["acceptance_report"] = item.AcceptanceReport,
        ["version"] = item.Version
    };

    private static Dictionary<string, object?> ClaimSnapshot(ItemRow row) => new()
    {
        ["claim_holder"] = row.ClaimHolder,
        ["claim_marker"] = row.ClaimMarker,
        ["claim_expires_at"] = row.ClaimExpiresAt is { } expires ? Utc(expires) : null
    };

    private async Task<ItemView> UpdateItemCoreAsync(Guid itemId, long expectedVersion, ItemPatchSpec patch, WorkActor actor, CancellationToken ct = default)
    {
        // A project id never changes, so it can be read before the write's transaction starts.
        Guid projectId;
        await using (var lookup = await OpenAsync(ct))
        {
            projectId = await lookup.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT project_id FROM work_items WHERE id = @itemId", new { itemId }, cancellationToken: ct))
                ?? throw WorkException.NotFound($"There is no item {itemId}.");
        }

        var addsDependencies = patch.AddBlockedBy is { Count: > 0 };
        // Two adds can each pass the cycle check and together close a loop, even when they share no
        // item, so dependency adds run one at a time under a named lock. Do not replace it with a
        // plain read followed by a write.
        return await RunAsync(addsDependencies ? [DependencyLockName] : [], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // Taken only when a session claims the item (handover of its open messages), after the row locks below.
            await using var messageLock = new MessageLockScope(connection, transaction);

            // An update that names a repository, or stores save points (S5: their scope reads the
            // membership and their rows reference the project), holds the project's row first, like
            // the project update that may drop that repository: project, then items, then knowledge.
            if (patch.Repository is not null || patch.Knowledge is { Count: > 0 })
                await HoldProjectAsync(connection, transaction, projectId, ct);

            // The rows of the item and of the blockers it adds, held in key order before anything is
            // read. Without it the INSERT of an edge takes a shared lock on each row for its foreign
            // keys one after the other, which deadlocked in KanbanBoard with writers holding them in
            // the other order.
            var held = new List<Guid> { itemId };
            held.AddRange(patch.AddBlockedBy ?? []);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version WHERE id IN @held", new { held = held.Distinct().ToArray() }, transaction, cancellationToken: ct));

            // A session's write on an item whose claim it lost (§5.9): refused claim_lost naming the
            // holder now, unless it reclaims the unclaimed item or lets go of it. A session that still
            // holds its claim is never refused: its heartbeat renewed the claim before this call.
            var effective = patch;
            var abandonLapsed = false;
            var current = (await ReadItemRowAsync(connection, transaction, itemId, ct))!;
            if (actor.SessionId is { } sessionId && await HasOpenSessionClaimAsync(connection, transaction, sessionId, itemId, ct))
            {
                var heldByMe = current.ClaimSessionId == sessionId && current.HasLiveClaim(now);
                if (!heldByMe)
                {
                    var reclaim = effective.Claim is { Action: ClaimAction.Claim } && !current.HasLiveClaim(now);
                    var release = effective.Claim is { Action: ClaimAction.Release };
                    if (!reclaim && !release)
                        throw await ClaimLostAsync(connection, transaction, current, now, ct);

                    if (release && current.ClaimSessionId != sessionId)
                    {
                        // Letting go of a claim that is no longer the session's: the item's claim is
                        // someone else's (or none) and stays as it is.
                        await CloseSessionClaimAsync(connection, transaction, sessionId, itemId, now, ct);
                        // The session's attempt, still active because nobody claimed the item since
                        // its claim lapsed, ends with this release: an item write, versioned and fed.
                        abandonLapsed = (await ActiveAttemptAsync(connection, transaction, itemId, ct))?.SessionId == sessionId;
                        if (effective.IsEmptyBesidesClaim && !abandonLapsed)
                        {
                            var unchanged = await ToViewAsync(connection, transaction, current, ct);
                            await transaction.CommitAsync(ct);
                            return unchanged;
                        }

                        effective = effective with { Claim = null };
                    }
                }
            }

            // A retried claim whose first answer was lost: the caller holds the claim already, so the
            // retry answers it rather than conflict on the version the first call advanced.
            if (effective.IsClaimOnly && current.Version != expectedVersion && current.HasLiveClaim(now) &&
                current.ClaimHolder == actor.Name && string.Equals(current.ClaimMarker, effective.Claim!.Marker, StringComparison.Ordinal))
            {
                var holding = await ToViewAsync(connection, transaction, current, ct);
                await transaction.CommitAsync(ct);
                return holding;
            }

            await AdvanceAsync(connection, transaction, "work_items", itemId, expectedVersion, now,
                () => LoadItemViewAsync(connection, transaction, itemId, ct), "Item", ct);

            var beforeRow = (await ReadItemRowAsync(connection, transaction, itemId, ct))!;
            var before = await ToViewAsync(connection, transaction, beforeRow, ct);
            var project = (await ReadProjectRowAsync(connection, transaction, projectId, ct))!;
            var extra = new JsonArray();

            var patchedRepository = effective.Repository is { } repository
                ? await RequireOwnedRepositoryAsync(connection, transaction, project, repository, ct)
                : null;
            if (effective.FeatureId is { } featureId)
                await RequireFeatureAsync(connection, transaction, project, featureId, ct);

            var handOver = false;
            var status = effective.Status ?? before.Status;
            var claimFields = ApplyClaim(beforeRow, effective.Claim, status, actor, now);

            // Planning fields (sc-27868). A ref names the item in its feature's plan, so it goes with
            // a move to another feature; inherits must name requirements of the feature it ends in.
            var finalFeature = effective.ClearFeature ? null : effective.FeatureId ?? beforeRow.FeatureId;
            var featureChanged = finalFeature != beforeRow.FeatureId;
            var storedCriteria = ReadList<AcceptanceCriterionView>(beforeRow.AcceptanceJson);
            var criteria = effective.ClearAcceptanceCriteria ? []
                : effective.AcceptanceCriteria is { } givenCriteria ? NormalizeCriteria(storedCriteria, givenCriteria)
                : storedCriteria;
            var inherits = effective.ClearInherits ? [] : effective.Inherits ?? ReadList<string>(beforeRow.InheritsJson);
            if (inherits.Count > 0 && (effective.Inherits is not null || featureChanged))
                await RequireInheritsAsync(connection, transaction, finalFeature, inherits, effective.Inherits is not null ? "inherits" : "feature (the item's inherits)", ct);
            var reportJson = beforeRow.AcceptanceReportJson;
            if (effective.AcceptanceReport is { } report)
            {
                if (status != WorkStatus.Complete)
                    throw WorkException.Invalid($"acceptance_report goes with marking {before.Key} complete (status complete), or on a complete item; it is {WorkStatuses.Name(status)}. Nothing was changed.");
                criteria = ApplyReport(criteria, report, before.Key);
                reportJson = JsonSerializer.Serialize(
                    new AcceptanceReportView(actor.Name, now, report.Summary, report.Criteria.Count == 0 ? null : report.Criteria, actor.SessionId), WorkJson.Options);
            }

            // Merge evidence only flags an item; completing it (the agent's call) clears the flag.
            var merged = status == WorkStatus.Complete ? false : effective.MergedAwaitingAcceptance ?? beforeRow.MergedAwaitingAcceptance;
            if (WorkStatuses.IsFinished(status))
            {
                await ReleaseSessionClaimsAsync(connection, transaction, itemId, now, ct);
            }
            else if (actor.SessionId is { } claimingSession)
            {
                if (effective.Claim is { Action: ClaimAction.Claim })
                {
                    await OpenSessionClaimAsync(connection, transaction, claimingSession, itemId, now, ct);
                    handOver = true;
                }
                else if (effective.Claim is { Action: ClaimAction.Release })
                    await CloseSessionClaimAsync(connection, transaction, claimingSession, itemId, now, ct);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET title = @title, description = @description, item_type = @type, status = @status, priority = @priority, " +
                "sort_order = @order, labels = @labels, repository_name = @repository, feature_id = @featureId, " +
                "claim_holder = @ClaimHolder, claim_marker = @ClaimMarker, claim_session_id = @ClaimSessionId, claimed_at = @ClaimedAt, claim_expires_at = @ClaimExpiresAt, " +
                "plan_ref = @planRef, goal = @goal, acceptance_json = @acceptance, inherits_json = @inherits, touches_json = @touches, " +
                "acceptance_report_json = @reportJson, merged_awaiting_acceptance = @merged " +
                "WHERE id = @itemId",
                new
                {
                    title = effective.Title ?? before.Title,
                    description = effective.ClearDescription ? null : effective.Description ?? before.Description,
                    type = JsonName(effective.Type ?? before.Type),
                    status = WorkStatuses.Name(status),
                    priority = JsonName(effective.Priority ?? before.Priority),
                    order = effective.Order ?? before.Order,
                    labels = effective.ClearLabels ? null : effective.Labels is { } labels ? JoinLabels(labels) : beforeRow.Labels,
                    repository = effective.ClearRepository ? null : patchedRepository ?? before.Repository,
                    featureId = effective.ClearFeature ? null : effective.FeatureId ?? beforeRow.FeatureId,
                    claimFields.ClaimHolder, claimFields.ClaimMarker, claimFields.ClaimSessionId, claimFields.ClaimedAt, claimFields.ClaimExpiresAt,
                    planRef = featureChanged ? null : beforeRow.PlanRef,
                    goal = effective.ClearGoal ? null : effective.Goal ?? beforeRow.Goal,
                    acceptance = WriteList(criteria),
                    inherits = WriteList(inherits),
                    touches = effective.ClearTouches ? null : effective.Touches is { } touches ? WriteList(touches) : beforeRow.TouchesJson,
                    reportJson,
                    merged,
                    itemId
                },
                transaction, cancellationToken: ct));

            // The execution attempt (sc-27868): a claim starts one (or continues the holder's own),
            // links and handoffs made now belong to it, and finishing or releasing ends it.
            var attempt = effective.Claim is { Action: ClaimAction.Claim } taken
                ? await BeginAttemptAsync(connection, transaction, beforeRow, actor, taken.Marker, now, extra, ct)
                : await ActiveAttemptAsync(connection, transaction, itemId, ct);

            await AddDependenciesAsync(connection, transaction, project, itemId, effective.AddBlockedBy ?? [], before, extra, now, ct);
            await RemoveDependenciesAsync(connection, transaction, itemId, effective.RemoveBlockedBy ?? [], before, extra, ct);
            await WriteLinksAsync(connection, transaction, itemId, effective.AddLinks ?? [], effective.RemoveLinks ?? [], actor, extra, now, ct, attempt?.Id);
            if (effective.Comment is { } comment)
                await AddCommentAsync(connection, transaction, itemId, comment, actor, extra, now, ct);
            if (effective.Handoff is { } handoff)
            {
                var written = await InsertHandoffAsync(connection, transaction, itemId, handoff, actor, now, ct);
                extra.Add(Change("handoff", null, written.Id.ToString()));
                if (attempt is not null)
                    await AttachHandoffAsync(connection, transaction, attempt, written.Id, handoff.Branch, ct);
            }

            if (attempt is not null)
            {
                if (WorkStatuses.IsFinished(status))
                    await FinishAttemptAsync(connection, transaction, attempt, before.Key, status, actor, now, extra, ct);
                else if (effective.Claim is { Action: ClaimAction.Release })
                    await EndAttemptAsync(connection, transaction, attempt, AttemptOutcome.Abandoned, $"The claim was released by {actor.Name}.", now, extra, ct);
                else if (abandonLapsed && attempt.SessionId == actor.SessionId)
                    await EndAttemptAsync(connection, transaction, attempt, AttemptOutcome.Abandoned, "The session's claim lapsed and the session let go of the item.", now, extra, ct);
            }

            // The save-point fields (S5): in this transaction, linked to the item, scoped by default
            // to the repository the item has after this write.
            if (effective.Knowledge is { Count: > 0 } knowledge)
            {
                await InsertKnowledgeAsync(connection, transaction, project, knowledge, itemId, null,
                    effective.ClearRepository ? null : patchedRepository ?? before.Repository, actor, now, ct);
                extra.Add(Change("knowledge", null, knowledge.Select(entry => entry.Id.ToString()).ToList()));
            }

            var afterRow = (await ReadItemRowAsync(connection, transaction, itemId, ct))!;
            var after = await ToViewAsync(connection, transaction, afterRow, ct);
            var beforeSnapshot = Snapshot(before);
            beforeSnapshot["version"] = expectedVersion;
            var changes = Diff(beforeSnapshot, Snapshot(after));
            foreach (var change in Diff(ClaimSnapshot(beforeRow), ClaimSnapshot(afterRow)))
                changes.Add(change!.DeepClone());
            foreach (var change in extra)
                changes.Add(change!.DeepClone());

            // Last, after every row this write locks: the messages lock is then taken with no row lock to follow.
            if (handOver)
                await HandOverOpenMessagesAsync(connection, transaction, messageLock, itemId, actor, now, ct);
            await CommitAsync(connection, transaction,
                [new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Item, after.Id, after.Key, WorkChangeKinds.ItemUpdated, changes)],
                actor, now, ct);
            return after;
        }, ct);
    }

    private sealed record ClaimFields(string? ClaimHolder, string? ClaimMarker, string? ClaimSessionId, DateTime? ClaimedAt, DateTime? ClaimExpiresAt);

    // Finishing an item (by anyone: an agent, a person, GitHub evidence, an import) ends every
    // session's claim on it: a session that held it and writes later is not told claim_lost for a
    // claim that was never taken from it (E1).
    private static Task ReleaseSessionClaimsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_session_claims SET released_at = @now WHERE item_id = @itemId AND released_at IS NULL",
            new { itemId, now }, transaction, cancellationToken: ct));

    /// <summary>Finishing an item ends its active attempt, complete or closed as the item is (an "attempt" change).</summary>
    private static Task FinishAttemptAsync(MySqlConnection connection, MySqlTransaction transaction, AttemptRow attempt, string key, WorkStatus status, WorkActor actor,
        DateTime now, JsonArray changes, CancellationToken ct) =>
        EndAttemptAsync(connection, transaction, attempt, status == WorkStatus.Complete ? AttemptOutcome.Complete : AttemptOutcome.Closed,
            $"{key} was marked {WorkStatuses.Name(status)} by {actor.Name}.", now, changes, ct);

    // KanbanBoard's claim rules, decided while the transaction holds the item's row:
    // claim succeeds when the item is unclaimed, its claim lapsed, or the same identity and marker
    // hold it (the expiry is reset); renew needs the caller's live claim; release clears the
    // caller's stored claim, live or lapsed. Finishing an item (complete, closed) releases it.
    // A session's claim lasts while the session is live: its expiry is the heartbeat deadline.
    private ClaimFields ApplyClaim(ItemRow row, ClaimSpec? claim, WorkStatus status, WorkActor actor, DateTime now)
    {
        var current = new ClaimFields(row.ClaimHolder, row.ClaimMarker, row.ClaimSessionId, row.ClaimedAt, row.ClaimExpiresAt);
        var live = row.HasLiveClaim(now);
        var mine = row.ClaimHolder == actor.Name && claim is not null && string.Equals(row.ClaimMarker, claim.Marker, StringComparison.Ordinal);
        string Holder() => $"{row.ClaimHolder} (marker '{row.ClaimMarker}') until {Utc(row.ClaimExpiresAt!.Value):O}";

        if (claim is not null)
        {
            switch (claim.Action)
            {
                case ClaimAction.Claim:
                    if (live && !mine)
                        throw new WorkException(WorkErrorCodes.AlreadyClaimed, $"{row.Key} is claimed by {Holder()}. Nothing was changed.");
                    if (WorkStatuses.IsFinished(status))
                        throw WorkException.Invalid($"{row.Key} is {WorkStatuses.Name(status)}: a finished item cannot be claimed. Nothing was changed.");
                    current = new ClaimFields(actor.Name, claim.Marker, actor.SessionId, now, now.AddSeconds(ClaimTtl(claim.TtlSeconds, actor)));
                    break;
                case ClaimAction.Renew:
                    if (live && !mine)
                        throw new WorkException(WorkErrorCodes.AlreadyClaimed, $"{row.Key} is claimed by {Holder()}. Nothing was changed.");
                    if (!live)
                        throw new WorkException(WorkErrorCodes.Conflict, $"You hold no live claim on {row.Key} under marker '{claim.Marker}': it lapsed, was released, or never existed. Claim it again. Nothing was changed.");
                    current = current with { ClaimExpiresAt = now.AddSeconds(ClaimTtl(claim.TtlSeconds, actor)) };
                    break;
                case ClaimAction.Release:
                    if (live && !mine)
                        throw new WorkException(WorkErrorCodes.AlreadyClaimed, $"{row.Key} is claimed by {Holder()}. Nothing was changed.");
                    if (!mine)
                        throw new WorkException(WorkErrorCodes.Conflict, $"You hold no claim on {row.Key} under marker '{claim.Marker}'. Nothing was changed.");
                    current = new ClaimFields(null, null, null, null, null);
                    break;
            }
        }

        return WorkStatuses.IsFinished(status) ? new ClaimFields(null, null, null, null, null) : current;
    }

    private int ClaimTtl(int ttlSeconds, WorkActor actor) => actor.SessionId is null ? ttlSeconds : idleSeconds;

    private async Task AddDependenciesAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, Guid itemId, IReadOnlyList<Guid> blockers, ItemView before, JsonArray extra, DateTime now, CancellationToken ct)
    {
        if (blockers.Count == 0)
            return;

        await RequireItemsOfProjectAsync(connection, transaction, project, blockers, ct);
        foreach (var blocker in blockers.Distinct())
        {
            if (before.BlockedBy.Any(existing => existing.Id == blocker))
                continue;

            if (blocker == itemId || await IsBlockedByAsync(connection, transaction, blocker, itemId, ct))
            {
                var blockerKey = await KeyOfAsync(connection, transaction, blocker, ct);
                throw new WorkException(WorkErrorCodes.Cycle,
                    $"{before.Key} blocked by {blockerKey} would make an item block itself, directly or through other items. Nothing was changed.");
            }

            await InsertEdgesAsync(connection, transaction, [(itemId, blocker)], now, ct);
            extra.Add(Change("blocked_by", null, await KeyOfAsync(connection, transaction, blocker, ct)));
        }
    }

    private static async Task RemoveDependenciesAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, IReadOnlyList<Guid> blockers, ItemView before, JsonArray extra, CancellationToken ct)
    {
        foreach (var blocker in blockers.Distinct())
        {
            var existing = before.BlockedBy.FirstOrDefault(candidate => candidate.Id == blocker);
            if (existing is null)
                continue;

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM work_item_dependencies WHERE blocked_item_id = @itemId AND blocker_item_id = @blocker",
                new { itemId, blocker }, transaction, cancellationToken: ct));
            extra.Add(Change("blocked_by", existing.Key, null));
        }
    }

    /// <summary>
    /// Whether <paramref name="item"/> is blocked by <paramref name="candidateBlocker"/>, directly
    /// or through other items: the one rule for a cycle. Making the candidate blocked by the item
    /// would then close a loop. Runs under the dependency lock.
    /// </summary>
    private static async Task<bool> IsBlockedByAsync(MySqlConnection connection, MySqlTransaction transaction, Guid item, Guid candidateBlocker, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { item };
        var frontier = new List<Guid> { item };
        while (frontier.Count > 0)
        {
            var blockers = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT blocker_item_id FROM work_item_dependencies WHERE blocked_item_id IN @frontier",
                new { frontier = frontier.ToArray() }, transaction, cancellationToken: ct))).ToList();
            if (blockers.Contains(candidateBlocker))
                return true;

            frontier = blockers.Where(seen.Add).ToList();
        }

        return false;
    }

    private static async Task<string> KeyOfAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, CancellationToken ct)
    {
        var row = await connection.QuerySingleAsync<(string ProjectKey, int Number)>(new CommandDefinition(
            "SELECT p.project_key, i.number FROM work_items i JOIN work_projects p ON p.id = i.project_id WHERE i.id = @itemId",
            new { itemId }, transaction, cancellationToken: ct));
        return WorkKeys.Item(row.ProjectKey, row.Number);
    }

    private static async Task WriteLinksAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, IReadOnlyList<LinkSpec> add, IReadOnlyList<LinkSpec> remove, WorkActor actor, JsonArray extra, DateTime now, CancellationToken ct,
        Guid? attemptId = null, IReadOnlySet<string>? historicalUrls = null)
    {
        var importing = actor.Name.StartsWith("import:", StringComparison.Ordinal);
        // The item's row is held, so the link writes of one item run one at a time and two
        // identical adds never meet on the unique key.
        foreach (var link in remove)
        {
            var kind = JsonName(link.Kind);
            var urlHash = HashUrl(link.Url);
            var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM work_item_links WHERE item_id = @itemId AND kind = @kind AND url_hash = @urlHash",
                new { itemId, kind, urlHash }, transaction, cancellationToken: ct));
            if (existing is not { } id)
                continue;

            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM work_item_links WHERE id = @id", new { id }, transaction, cancellationToken: ct));
            extra.Add(Change("link", new { id, kind, url = link.Url }, null));
        }

        foreach (var link in add)
        {
            var kind = JsonName(link.Kind);
            var urlHash = HashUrl(link.Url);
            var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM work_item_links WHERE item_id = @itemId AND kind = @kind AND url_hash = @urlHash",
                new { itemId, kind, urlHash }, transaction, cancellationToken: ct)) > 0;
            if (exists)
            {
                // A link an import wrote, added again by GitHub evidence or an agent, becomes theirs
                // (sc-27876): it is no longer a historical PR of the source.
                if (!importing)
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE work_item_links SET created_by = @createdBy, historical = 0, attempt_id = COALESCE(@attemptId, attempt_id) " +
                        "WHERE item_id = @itemId AND kind = @kind AND url_hash = @urlHash AND created_by LIKE 'import:%'",
                        new { itemId, kind, urlHash, createdBy = actor.Name, attemptId }, transaction, cancellationToken: ct));
                continue;
            }

            var id = Guid.NewGuid();
            var historical = importing && historicalUrls?.Contains(link.Url) == true;
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_item_links (id, item_id, kind, url, url_hash, created_by, created_at, attempt_id, historical) VALUES (@id, @itemId, @kind, @Url, @urlHash, @createdBy, @now, @attemptId, @historical)",
                new { id, itemId, kind, link.Url, urlHash, createdBy = actor.Name, now, attemptId, historical }, transaction, cancellationToken: ct));
            extra.Add(Change("link", null, attemptId is null ? new { id, kind, url = link.Url } : new { id, kind, url = link.Url, attempt = attemptId }));
        }
    }

    private static async Task AddCommentAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, string body, WorkActor actor, JsonArray extra, DateTime now, CancellationToken ct)
    {
        // Every comment of an item is later than the ones committed before it, whatever the clocks
        // of the writers say (the item's row is held, so comment writes of one item run one at a
        // time): a reader paging comments by time never has a new one land behind it.
        var latest = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(created_at) FROM work_item_comments WHERE item_id = @itemId", new { itemId }, transaction, cancellationToken: ct));
        var createdAt = latest is { } last && last >= now ? last.AddTicks(10) : now;
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_item_comments (id, item_id, author, author_kind, author_session_id, body, created_at) VALUES (@id, @itemId, @author, @kind, @session, @body, @createdAt)",
            new { id, itemId, author = actor.Name, kind = actor.Kind, session = actor.SessionId, body, createdAt }, transaction, cancellationToken: ct));
        extra.Add(Change("comment", null, new { id, author = actor.Name, body }));
    }

    // ---- Plans -------------------------------------------------------------------------------

    private sealed record StoredPlanResult(Dictionary<string, CreatedRef> Features, Dictionary<string, CreatedRef> Items);

    public Task<PlanResult> ImportPlanAsync(Guid projectId, PlanSpec plan, WorkActor actor, string keyHash, CancellationToken ct = default) =>
        // The plan's items are all new, so its edges can only name each other and the service has
        // already checked them for a cycle: no dependency lock. The import holds the project's row
        // like a create, so its numbers follow the earlier creates without gaps.
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var project = await HoldProjectAsync(connection, transaction, projectId, ct);
            if (await FindKeyAsync(connection, transaction, projectId, PlanOperation, keyHash, ct) is { ResultJson: { } stored })
            {
                // The answer of the first import, whatever has happened to its objects since.
                var earlier = JsonSerializer.Deserialize<StoredPlanResult>(stored, WorkJson.Options)!;
                return new PlanResult(false, project.Id, project.ProjectKey, earlier.Features, earlier.Items);
            }

            var owned = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT repository_name FROM work_project_repositories WHERE project_id = @projectId AND role = 'owned'",
                new { projectId }, transaction, cancellationToken: ct))).ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.Items)
            {
                if (item.Repository is { } repository && !owned.ContainsKey(repository))
                    throw WorkException.Invalid($"Item '{item.Ref}': '{repository}' is not an owned repository of {project.ProjectKey}. Nothing was created.");
            }

            var features = new Dictionary<string, CreatedRef>(StringComparer.Ordinal);
            var featureIds = new List<Guid>();
            for (var index = 0; index < plan.Features.Count; index++)
            {
                var number = project.NextFeatureNumber + index;
                var id = await InsertFeatureAsync(connection, transaction, project, number, plan.Features[index].Feature, actor, now, ct);
                featureIds.Add(id);
                features[plan.Features[index].Ref] = new CreatedRef(id, WorkKeys.Feature(project.ProjectKey, number));
            }

            var items = new Dictionary<string, CreatedRef>(StringComparer.Ordinal);
            var itemIds = plan.Items.Select(_ => Guid.NewGuid()).ToList();
            for (var index = 0; index < plan.Items.Count; index++)
            {
                var item = plan.Items[index];
                var number = project.NextItemNumber + index;
                // Each item a microsecond after the one before it, so lists ordered by creation
                // return them in the order of the plan.
                await InsertItemAsync(connection, transaction, project.Id, itemIds[index], number, item.Title, item.Type, item.Status, item.Priority,
                    item.Description, item.Labels, item.Repository is { } itemRepository ? owned[itemRepository] : null, item.FeatureIndex is { } featureIndex ? featureIds[featureIndex] : null,
                    item.Order, actor, now.AddTicks(index * 10), ct, (item.Plan ?? new ItemPlanFields()) with { Ref = item.Ref });
                items[item.Ref] = new CreatedRef(itemIds[index], WorkKeys.Item(project.ProjectKey, number));
            }

            await InsertEdgesAsync(connection, transaction,
                plan.Items.SelectMany((item, index) => item.BlockedBy.Distinct().Select(blocker => (itemIds[index], itemIds[blocker]))), now, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_projects SET next_item_number = next_item_number + @items, next_feature_number = next_feature_number + @features WHERE id = @projectId",
                new { items = plan.Items.Count, features = plan.Features.Count, projectId }, transaction, cancellationToken: ct));
            await InsertKeyAsync(connection, transaction, projectId, PlanOperation, keyHash, null,
                JsonSerializer.Serialize(new StoredPlanResult(features, items), WorkJson.Options), now, ct);

            // The features before the items that name them, in the order of the plan.
            var entries = new List<PendingChange>();
            foreach (var featureId in featureIds)
                entries.Add(FeatureCreatedEntry((await LoadFeatureViewAsync(connection, transaction, featureId, withCounts: false, ct))!));
            foreach (var itemId in itemIds)
                entries.Add(ItemCreatedEntry((await LoadItemViewAsync(connection, transaction, itemId, ct))!));

            await CommitAsync(connection, transaction, entries, actor, now, ct);
            return new PlanResult(true, project.Id, project.ProjectKey, features, items);
        }, ct);

    // ---- next_work ---------------------------------------------------------------------------

    public async Task<NextWorkResult> NextWorkAsync(NextWorkSpec spec, WorkActor actor, CancellationToken ct = default)
    {
        // The calls of one identity in one project run one at a time, so the look-up of its key and
        // of its existing claim cannot race its own retry. Between identities the conditional
        // UPDATE of the claim decides: two callers never get one item.
        var lockName = $"next_work\n{actor.Name}\n{spec.ProjectId}";
        return await RunAsync([lockName], async connection =>
        {
            var now = Now();
            if (await ReadProjectRowAsync(connection, null, spec.ProjectId, ct) is null)
                throw WorkException.NotFound($"There is no project {spec.ProjectId}.");

            if (spec.KeyHash is { } keyHash && await FindKeyAsync(connection, null, spec.ProjectId, NextWorkOperation, keyHash, ct) is { } earlier)
            {
                if (earlier.EntityId is not { } allocated)
                    return new NextWorkResult(null, Reused: true, Reason: "no_ready_work");

                // The same allocation as the first call, while the caller still holds it. When its
                // claim lapsed and passed on (or ended), the repeat says so instead of answering an
                // item that is no longer the caller's.
                var row = (await ReadItemRowAsync(connection, null, allocated, ct))!;
                var stillMine = row.HasLiveClaim(now) && row.ClaimHolder == actor.Name && string.Equals(row.ClaimMarker, spec.Marker, StringComparison.Ordinal);
                if (stillMine || WorkStatuses.IsFinished(WorkStatuses.Parse(row.Status)))
                    return new NextWorkResult(await ToViewAsync(connection, null, row, ct), Reused: true);

                throw await ClaimLostAsync(connection, null, row, now, ct);
            }

            if (!spec.Another)
            {
                var existing = await connection.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM work_items WHERE project_id = @ProjectId AND claim_holder = @holder AND claim_marker = @Marker " +
                    "AND claim_expires_at > @now AND status NOT IN ('complete', 'closed') ORDER BY claimed_at, number LIMIT 1",
                    new { spec.ProjectId, holder = actor.Name, spec.Marker, now }, cancellationToken: ct));
                if (existing is { } claimed)
                {
                    if (spec.KeyHash is { } existingKey)
                    {
                        await using var keyTransaction = await connection.BeginTransactionAsync(ct);
                        await InsertKeyAsync(connection, keyTransaction, spec.ProjectId, NextWorkOperation, existingKey, claimed, null, now, ct);
                        await keyTransaction.CommitAsync(ct);
                    }

                    return new NextWorkResult(await LoadItemViewAsync(connection, null, claimed, ct), Reused: true);
                }
            }

            // Recoverable items (in progress, claim lapsed or released) before fresh ready ones,
            // with the same filters (§5.9). Every lost race means another caller claimed an item,
            // so the rounds make progress.
            foreach (var recover in new[] { true, false })
            {
                for (var round = 0; round < 10; round++)
                {
                    var candidates = await CandidatesAsync(connection, spec, recover ? RecoverableCondition : AvailableCondition, now, ct);
                    if (candidates.Count == 0)
                        break;

                    foreach (var candidate in candidates)
                    {
                        if (await TryClaimAsync(connection, candidate, recover, spec, actor, ct) is { } claimed)
                            return claimed;
                    }

                    if (round == 9)
                        throw WorkException.Busy("Other callers took every item this call tried, ten times over. Nothing was claimed; try again.");
                }
            }

            return new NextWorkResult(null, Reused: false, Reason: "no_ready_work");
        }, ct);
    }

    private static async Task<IReadOnlyList<Guid>> CandidatesAsync(MySqlConnection connection, NextWorkSpec spec, string condition, DateTime now, CancellationToken ct)
    {
        var parameters = new DynamicParameters(new { spec.ProjectId, now });
        var where = new List<string> { "i.project_id = @ProjectId", condition };
        if (spec.Repository is { } repository)
        {
            where.Add("i.repository_name = @repository");
            parameters.Add("repository", repository);
        }

        if (spec.FeatureId is { } featureId)
        {
            where.Add("i.feature_id = @featureId");
            parameters.Add("featureId", featureId);
        }

        if (spec.Type is { } type)
        {
            where.Add("i.item_type = @type");
            parameters.Add("type", JsonName(type));
        }

        if (spec.Label is { } label)
        {
            where.Add(LabelCondition);
            parameters.Add("label", label);
        }

        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            $"SELECT i.id FROM work_items i WHERE {string.Join(" AND ", where)} ORDER BY i.priority_rank, i.sort_order, i.created_at, i.number LIMIT 25",
            parameters, cancellationToken: ct))).ToList();
    }

    // Claims one candidate, or returns null when another caller got it first (or it stopped being
    // available). The row is held first, so the read after it reports the claim the UPDATE
    // replaces; the blockers are read with a locking read, so a blocker reopened at the same time
    // either commits first and is seen, or waits for this claim. A recovered item keeps its status
    // and answers what its previous holder left (read before the claim is replaced).
    private async Task<NextWorkResult?> TryClaimAsync(MySqlConnection connection, Guid itemId, bool recover, NextWorkSpec spec, WorkActor actor, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var now = Now();
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using var messageLock = new MessageLockScope(connection, transaction);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET version = version WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
                var before = (await ReadItemRowAsync(connection, transaction, itemId, ct))!;
                var unfinished = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM work_item_dependencies d JOIN work_items b ON b.id = d.blocker_item_id " +
                    "WHERE d.blocked_item_id = @itemId AND b.status <> 'complete' LOCK IN SHARE MODE",
                    new { itemId }, transaction, cancellationToken: ct));
                if (unfinished > 0 || await HasOpenBlockingQuestionAsync(connection, transaction, itemId, ct))
                    return null;

                var status = recover ? "in_progress" : "ready";
                var expiresAt = now.AddSeconds(ClaimTtl(spec.TtlSeconds, actor));
                var claimed = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET claim_holder = @holder, claim_marker = @Marker, claim_session_id = @session, claimed_at = @now, " +
                    "claim_expires_at = @expiresAt, version = version + 1 " +
                    "WHERE id = @itemId AND status = @status AND (claim_holder IS NULL OR claim_expires_at IS NULL OR claim_expires_at <= @now)",
                    new { holder = actor.Name, spec.Marker, session = actor.SessionId, now, expiresAt, itemId, status }, transaction, cancellationToken: ct));
                if (claimed == 0)
                    return null;

                var recovery = recover ? await LoadRecoveryAsync(connection, transaction, before, ct) : null;
                if (actor.SessionId is { } sessionId)
                    await OpenSessionClaimAsync(connection, transaction, sessionId, itemId, now, ct);
                var attemptChanges = new JsonArray();
                await BeginAttemptAsync(connection, transaction, before, actor, spec.Marker, now, attemptChanges, ct);
                if (spec.KeyHash is { } keyHash)
                    await InsertKeyAsync(connection, transaction, spec.ProjectId, NextWorkOperation, keyHash, itemId, null, now, ct);

                var afterRow = (await ReadItemRowAsync(connection, transaction, itemId, ct))!;
                var after = await ToViewAsync(connection, transaction, afterRow, ct);
                var changes = Diff(ClaimSnapshot(before), ClaimSnapshot(afterRow));
                changes.Add(Change("version", before.Version, afterRow.Version));
                foreach (var change in attemptChanges)
                    changes.Add(change!.DeepClone());
                // Last, after every row this write locks: the messages lock is then taken with no row lock to follow.
                await HandOverOpenMessagesAsync(connection, transaction, messageLock, itemId, actor, now, ct);
                await CommitAsync(connection, transaction,
                    [new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Item, after.Id, after.Key, recover ? WorkChangeKinds.Recovered : WorkChangeKinds.Claimed, changes)],
                    actor, now, ct);
                return new NextWorkResult(after with { OpenMessages = await ReadOpenMessagesAsync(connection, null, itemId, ct) }, Reused: false, Recovery: recovery);
            }
            catch (MySqlException exception) when (exception.Number is (MySqlDeadlock or MySqlRecordChanged) && attempt < DeadlockAttempts)
            {
                // Rolled back by the server; try the same candidate again.
            }
        }
    }

    // ---- Shared ------------------------------------------------------------------------------

    /// <summary>
    /// Holds the project's row until the transaction ends, as its first statement, and reads it: the
    /// creates of one project then run one at a time and each reads the counters after the one
    /// before it committed (a read before the hold would fix the REPEATABLE READ view too early).
    /// </summary>
    private static async Task<ProjectRow> HoldProjectAsync(MySqlConnection connection, MySqlTransaction transaction, Guid projectId, CancellationToken ct)
    {
        var held = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_projects SET version = version WHERE id = @projectId", new { projectId }, transaction, cancellationToken: ct));
        if (held == 0)
            throw WorkException.NotFound($"There is no project {projectId}.");
        return (await ReadProjectRowAsync(connection, transaction, projectId, ct))!;
    }

    /// <summary>
    /// The conditional versioned UPDATE: advances the version only when the object still has the
    /// expected one, and holds its row until the transaction ends. Of several writers passing the
    /// same expected_version exactly one succeeds; the others are refused conflict with the
    /// current object. Do not replace it with a read followed by a write.
    /// </summary>
    private static async Task AdvanceAsync(MySqlConnection connection, MySqlTransaction transaction, string table, Guid id, long expectedVersion, DateTime now, Func<Task<object?>> current, string noun, CancellationToken ct)
    {
        var advanced = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {table} SET version = version + 1, updated_at = @now WHERE id = @id AND version = @expectedVersion",
            new { now, id, expectedVersion }, transaction, cancellationToken: ct));
        if (advanced > 0)
            return;

        var state = await current();
        if (state is null)
            throw WorkException.NotFound($"There is no {noun.ToLowerInvariant()} {id}.");

        var version = state switch
        {
            ProjectView project => project.Version,
            FeatureView feature => feature.Version,
            ItemView item => item.Version,
            _ => 0
        };
        throw new WorkException(WorkErrorCodes.Conflict,
            $"{noun} is at version {version}, not {expectedVersion}: it was changed after it was read. Nothing was changed; merge with the current state (in current) and repeat the call.",
            state);
    }

    private static async Task AdvanceAsync<T>(MySqlConnection connection, MySqlTransaction transaction, string table, Guid id, long expectedVersion, DateTime now, Func<Task<T?>> current, string noun, CancellationToken ct)
        where T : class =>
        await AdvanceAsync(connection, transaction, table, id, expectedVersion, now, async () => (object?)await current(), noun, ct);

    // The owned repository of the project that <paramref name="repository"/> names (without regard
    // to case), in the spelling the project stores.
    private static async Task<string> RequireOwnedRepositoryAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, string repository, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT repository_name FROM work_project_repositories WHERE project_id = @projectId AND repository_name = @repository AND role = 'owned' LIMIT 1",
            new { projectId = project.Id, repository }, transaction, cancellationToken: ct))
        ?? throw WorkException.Invalid($"'{repository}' is not an owned repository of {project.ProjectKey}: an item's repository must be one. Nothing was changed.");

    private static async Task RequireFeatureAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, Guid featureId, CancellationToken ct)
    {
        var found = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_features WHERE id = @featureId AND project_id = @projectId",
            new { featureId, projectId = project.Id }, transaction, cancellationToken: ct));
        if (found == 0)
            throw WorkException.Invalid($"Feature {featureId} is not a feature of {project.ProjectKey}. Nothing was changed.");
    }

    private static async Task RequireItemsOfProjectAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, IReadOnlyList<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToArray();
        var found = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM work_items WHERE id IN @ids AND project_id = @projectId", new { ids, projectId = project.Id }, transaction, cancellationToken: ct))).ToHashSet();
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
            throw WorkException.Invalid($"{string.Join(", ", missing)} {(missing.Count == 1 ? "is not an item" : "are not items")} of {project.ProjectKey}: dependencies stay within a project. Nothing was changed.");
    }

    private sealed class KeyRow
    {
        public Guid? EntityId { get; init; }
        public string? ResultJson { get; init; }
    }

    private static Task<KeyRow?> FindKeyAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid scopeId, string operation, string keyHash, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<KeyRow>(new CommandDefinition(
            "SELECT entity_id, result_json FROM work_idempotency_keys WHERE scope_id = @scopeId AND operation = @operation AND key_hash = @keyHash",
            new { scopeId, operation, keyHash }, transaction, cancellationToken: ct));

    private static Task InsertKeyAsync(MySqlConnection connection, MySqlTransaction transaction, Guid scopeId, string operation, string keyHash, Guid? entityId, string? resultJson, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_idempotency_keys (scope_id, operation, key_hash, entity_id, result_json, created_at) VALUES (@scopeId, @operation, @keyHash, @entityId, @resultJson, @now)",
            new { scopeId, operation, keyHash, entityId, resultJson, now }, transaction, cancellationToken: ct));
}
