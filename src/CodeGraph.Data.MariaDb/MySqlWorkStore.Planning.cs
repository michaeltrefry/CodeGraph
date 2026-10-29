using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Feature planning, re-planning and completion (sc-27868): requirements, acceptance criteria,
// inherits and touches as small JSON lists on their rows; derived feature completion; execution
// attempts; sync_plan previews bound to versions and their all-or-nothing apply; and the
// changed_by_replan notices to the holders of re-planned items.
public sealed partial class MySqlWorkStore
{
    // ---- Stored lists ------------------------------------------------------------------------

    private static List<T> ReadList<T>(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<T>>(json, WorkJson.Options) ?? [];

    private static string? WriteList<T>(IReadOnlyList<T>? values) =>
        values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values, WorkJson.Options);

    /// <summary>
    /// The requirements of a feature after a write: <paramref name="requested"/> (when given) is the
    /// whole active list in order; an active requirement left out must be named in
    /// <paramref name="drops"/>, never dropped silently; a dropped one stays stored with its decision
    /// (and is active again when listed). Requirements are never deleted.
    /// </summary>
    private static List<RequirementView> MergeRequirements(IReadOnlyList<RequirementView> existing, IReadOnlyList<RequirementSpec>? requested, IReadOnlyList<RequirementDropSpec>? drops, string featureKey)
    {
        if (requested is null && drops is null)
            return existing.ToList();

        var dropById = (drops ?? []).ToDictionary(drop => drop.Id, StringComparer.Ordinal);
        foreach (var drop in dropById.Values)
        {
            if (existing.FirstOrDefault(requirement => requirement.Id == drop.Id) is not { State: RequirementState.Active })
                throw WorkException.Invalid($"drop_requirements names {drop.Id}, which is not an active requirement of {featureKey}. Nothing was changed.");
            if (requested?.Any(requirement => requirement.Id == drop.Id) == true)
                throw WorkException.Invalid($"{drop.Id} is both listed in requirements and named in drop_requirements. Nothing was changed.");
        }

        if (requested is null)
            return existing.Select(requirement => dropById.TryGetValue(requirement.Id, out var drop) ? requirement with { State = RequirementState.Dropped, Decision = drop.Decision } : requirement).ToList();

        var result = requested.Select(requirement => new RequirementView(requirement.Id, requirement.Text, RequirementState.Active)).ToList();
        foreach (var current in existing.Where(current => requested.All(requirement => requirement.Id != current.Id)))
        {
            if (current.State == RequirementState.Dropped)
                result.Add(current);
            else if (dropById.TryGetValue(current.Id, out var drop))
                result.Add(current with { State = RequirementState.Dropped, Decision = drop.Decision });
            else
                throw WorkException.Invalid(
                    $"{current.Id} is an active requirement of {featureKey} and is left out of requirements. Keep it, or drop it with drop_requirements (id and decision). Nothing was changed.");
        }

        return result;
    }

    private static void RequireInherits(IReadOnlyList<string> inherits, IReadOnlyList<RequirementView> requirements, string where)
    {
        var unknown = inherits.Where(id => requirements.All(requirement => requirement.Id != id)).ToList();
        if (unknown.Count > 0)
            throw WorkException.Invalid(
                $"{where} names {string.Join(", ", unknown)}, which {(unknown.Count == 1 ? "is not a requirement" : "are not requirements")} of the item's feature " +
                $"(it has {(requirements.Count == 0 ? "none" : string.Join(", ", requirements.Select(requirement => requirement.Id)))}). Nothing was changed.");
    }

    private static async Task RequireInheritsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid? featureId, IReadOnlyList<string> inherits, string where, CancellationToken ct)
    {
        if (featureId is not { } id)
            throw WorkException.Invalid($"{where}: an item without a feature inherits no requirements. Set feature, or clear inherits. Nothing was changed.");
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT requirements_json FROM work_features WHERE id = @id", new { id }, transaction, cancellationToken: ct));
        RequireInherits(inherits, ReadList<RequirementView>(json), where);
    }

    /// <summary>
    /// Criteria as stored: an id left out is the first free AC1, AC2, AC3; a criterion that keeps its
    /// id and text keeps its state and evidence unless the write sets them.
    /// </summary>
    private static List<AcceptanceCriterionView> NormalizeCriteria(IReadOnlyList<AcceptanceCriterionView> existing, IReadOnlyList<CriterionSpec> given)
    {
        var taken = given.Where(criterion => criterion.Id is not null).Select(criterion => criterion.Id!).ToHashSet(StringComparer.Ordinal);
        var next = 1;
        var result = new List<AcceptanceCriterionView>();
        foreach (var criterion in given)
        {
            var id = criterion.Id;
            if (id is null)
            {
                while (taken.Contains($"AC{next}"))
                    next++;
                id = $"AC{next}";
                taken.Add(id);
            }

            var prior = existing.FirstOrDefault(candidate => candidate.Id == id && candidate.Text == criterion.Text);
            result.Add(new AcceptanceCriterionView(
                id,
                criterion.Text,
                criterion.State ?? prior?.State ?? AcceptanceState.Unmet,
                criterion.Evidence ?? (criterion.State is null ? prior?.Evidence : null)));
        }

        return result;
    }

    private static List<AcceptanceCriterionView> ApplyReport(IReadOnlyList<AcceptanceCriterionView> criteria, AcceptanceReportSpec report, string key)
    {
        var result = criteria.ToList();
        foreach (var entry in report.Criteria)
        {
            var index = result.FindIndex(criterion => criterion.Id == entry.Id);
            if (index < 0)
                throw WorkException.Invalid(
                    $"acceptance_report.criteria names {entry.Id}, which is not an acceptance criterion of {key} (it has {(result.Count == 0 ? "none" : string.Join(", ", result.Select(criterion => criterion.Id)))}). Nothing was changed.");
            result[index] = result[index] with { State = entry.State, Evidence = entry.Evidence ?? result[index].Evidence };
        }

        return result;
    }

    // ---- Feature completion ------------------------------------------------------------------

    /// <summary>
    /// The derived status of a feature (§5.2, R11c): complete when it has items, every item is
    /// complete or closed, at least one is complete, and every active requirement is inherited by a
    /// complete item. Closing every item does not complete a feature. Read in the caller's snapshot.
    /// </summary>
    private static async Task<(FeatureStatus Status, FeatureCompletionView Completion, List<RequirementView> Requirements)> CompletionAsync(
        MySqlConnection connection, MySqlTransaction? transaction, FeatureRow row, CancellationToken ct)
    {
        var items = (await connection.QueryAsync<(int Number, string Status, string? InheritsJson)>(new CommandDefinition(
            "SELECT number, status, inherits_json FROM work_items WHERE feature_id = @id ORDER BY number",
            new { id = row.Id }, transaction, cancellationToken: ct)))
            .Select(item => (item.Number, item.Status, Inherits: ReadList<string>(item.InheritsJson))).ToList();
        var complete = items.Where(item => item.Status == "complete").ToList();
        var requirements = ReadList<RequirementView>(row.RequirementsJson)
            .Select(requirement => requirement with
            {
                CoveredBy = complete.Where(item => item.Inherits.Contains(requirement.Id)).Select(item => WorkKeys.Item(row.ProjectKey, item.Number)).ToList() is { Count: > 0 } keys ? keys : null
            })
            .ToList();
        var uncovered = requirements.Where(requirement => requirement is { State: RequirementState.Active, CoveredBy: null }).Select(requirement => requirement.Id).ToList();
        var finished = items.Count(item => item.Status is "complete" or "closed");
        var done = items.Count > 0 && finished == items.Count && complete.Count > 0 && uncovered.Count == 0;
        return (done ? FeatureStatus.Complete : FeatureStatus.Open,
            new FeatureCompletionView(items.Count, finished, complete.Count, uncovered.Count == 0 ? null : uncovered),
            requirements);
    }

    // ---- Attempts ----------------------------------------------------------------------------

    private sealed class AttemptRow
    {
        public Guid Id { get; init; }
        public Guid ItemId { get; init; }
        public int Number { get; init; }
        public string Holder { get; init; } = "";
        public string Marker { get; init; } = "";
        public string? SessionId { get; init; }
        public string? Branch { get; init; }
        public string Outcome { get; init; } = "";
        public string? Reason { get; init; }
        public Guid? HandoffId { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? EndedAt { get; init; }
        public string? Harness { get; init; }
        public string? Model { get; init; }
        public string? Host { get; init; }
    }

    private const string AttemptColumns =
        "a.id, a.item_id, a.number, a.holder, a.marker, a.session_id, a.branch, a.outcome, a.reason, a.handoff_id, a.started_at, a.ended_at, s.harness, s.model, s.host";

    private static Task<AttemptRow?> ActiveAttemptAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, CancellationToken ct) =>
        connection.QueryFirstOrDefaultAsync<AttemptRow>(new CommandDefinition(
            $"SELECT {AttemptColumns} FROM work_attempts a LEFT JOIN work_sessions s ON s.id = a.session_id " +
            "WHERE a.item_id = @itemId AND a.outcome = 'active' ORDER BY a.number DESC LIMIT 1",
            new { itemId }, transaction, cancellationToken: ct));

    private static JsonObject AttemptNode(AttemptRow attempt, AttemptOutcome outcome, string? reason = null)
    {
        var node = new JsonObject { ["id"] = attempt.Id.ToString(), ["number"] = attempt.Number, ["outcome"] = WorkJson.Name(outcome) };
        if (reason is not null)
            node["reason"] = reason;
        return node;
    }

    /// <summary>
    /// The attempt of a claim being taken: the holder's own active attempt continues; another
    /// holder's active attempt (whose claim lapsed or was let go) ends abandoned and a new one starts.
    /// </summary>
    private static async Task<AttemptRow> BeginAttemptAsync(MySqlConnection connection, MySqlTransaction transaction, ItemRow item, WorkActor actor, string marker, DateTime now, JsonArray changes, CancellationToken ct)
    {
        var active = await ActiveAttemptAsync(connection, transaction, item.Id, ct);
        if (active is not null)
        {
            var same = actor.SessionId is { } session
                ? active.SessionId == session
                : active.SessionId is null && active.Holder == actor.Name && active.Marker == marker;
            if (same)
                return active;

            var by = actor.SessionId is null ? actor.Name : $"{actor.Name} (session {actor.SessionId})";
            await EndAttemptAsync(connection, transaction, active, AttemptOutcome.Abandoned, $"Its claim lapsed or was let go, and {by} claimed the item.", now, changes, ct);
        }

        var number = (await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(number) FROM work_attempts WHERE item_id = @itemId", new { itemId = item.Id }, transaction, cancellationToken: ct)) ?? 0) + 1;
        string? branch = null;
        if (actor.SessionId is { } sessionId && Guid.TryParse(sessionId, out var parsed))
            branch = (await ReadSessionRowAsync(connection, transaction, parsed, ct))?.Branch;

        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_attempts (id, item_id, number, holder, marker, session_id, branch, outcome, started_at) " +
            "VALUES (@id, @itemId, @number, @holder, @marker, @session, @branch, 'active', @now)",
            new { id, itemId = item.Id, number, holder = actor.Name, marker, session = actor.SessionId, branch, now }, transaction, cancellationToken: ct));
        var started = (await ActiveAttemptAsync(connection, transaction, item.Id, ct))!;
        changes.Add(Change("attempt", null, AttemptNode(started, AttemptOutcome.Active)));
        return started;
    }

    private static async Task EndAttemptAsync(MySqlConnection connection, MySqlTransaction transaction, AttemptRow attempt, AttemptOutcome outcome, string reason, DateTime now, JsonArray changes, CancellationToken ct)
    {
        reason = reason.Length <= 1000 ? reason : reason[..999] + "…";
        var ended = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_attempts SET outcome = @outcome, reason = @reason, ended_at = @now WHERE id = @id AND outcome = 'active'",
            new { outcome = WorkJson.Name(outcome), reason, now, id = attempt.Id }, transaction, cancellationToken: ct));
        if (ended > 0)
            changes.Add(Change("attempt", AttemptNode(attempt, AttemptOutcome.Active), AttemptNode(attempt, outcome, reason)));
    }

    /// <summary>A handoff written while an attempt is active belongs to it; its branch becomes the attempt's.</summary>
    private static async Task AttachHandoffAsync(MySqlConnection connection, MySqlTransaction transaction, AttemptRow attempt, Guid handoffId, string? branch, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_handoffs SET attempt_id = @attempt WHERE id = @handoffId", new { attempt = attempt.Id, handoffId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_attempts SET handoff_id = @handoffId, branch = COALESCE(@branch, branch) WHERE id = @attempt",
            new { handoffId, branch, attempt = attempt.Id }, transaction, cancellationToken: ct));
    }

    /// <summary>The attempts of an item, newest first, each with the branch and PR links made during it.</summary>
    private static async Task<IReadOnlyList<AttemptView>> ReadAttemptsAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, int? limit, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<AttemptRow>(new CommandDefinition(
            $"SELECT {AttemptColumns} FROM work_attempts a LEFT JOIN work_sessions s ON s.id = a.session_id WHERE a.item_id = @itemId ORDER BY a.number DESC" +
            (limit is { } count ? $" LIMIT {count}" : string.Empty),
            new { itemId }, transaction, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
            return [];

        var links = (await connection.QueryAsync<(Guid AttemptId, string Kind, string Url)>(new CommandDefinition(
            "SELECT attempt_id, kind, url FROM work_item_links WHERE item_id = @itemId AND attempt_id IS NOT NULL AND kind IN ('branch', 'pr') ORDER BY created_at, id",
            new { itemId }, transaction, cancellationToken: ct))).ToList();
        return rows.Select(row =>
        {
            var branches = links.Where(link => link.AttemptId == row.Id && link.Kind == "branch").Select(link => link.Url).ToList();
            var prs = links.Where(link => link.AttemptId == row.Id && link.Kind == "pr").Select(link => link.Url).ToList();
            return new AttemptView(
                row.Id, row.Number, ParseName<AttemptOutcome>(row.Outcome),
                new HolderView(row.Holder, row.SessionId, row.Harness, row.Model, row.Host),
                Utc(row.StartedAt), row.EndedAt is { } ended ? Utc(ended) : null, row.Reason, row.Branch,
                branches.Count == 0 ? null : branches, prs.Count == 0 ? null : prs, row.HandoffId);
        }).ToList();
    }

    // ---- Notices -----------------------------------------------------------------------------

    private static Task InsertNoticeAsync(MySqlConnection connection, MySqlTransaction transaction, NoticeView notice, string? sessionId, string identity, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_notices (id, item_id, session_id, identity, kind, body_json, created_at) VALUES (@id, @itemId, @sessionId, @identity, @kind, @body, @now)",
            new
            {
                id = Guid.NewGuid(), itemId = notice.ItemId, sessionId, identity, kind = notice.Kind,
                body = JsonSerializer.Serialize(notice, WorkJson.Options), now = notice.CreatedAt
            },
            transaction, cancellationToken: ct));

    public async Task<IReadOnlyList<NoticeView>> TakeNoticesAsync(WorkActor actor, CancellationToken ct = default)
    {
        var (where, parameters) = actor.SessionId is { } session
            ? ("session_id = @session", new { session = (string?)session, identity = actor.Name })
            : ("session_id IS NULL AND identity = @identity", new { session = (string?)null, identity = actor.Name });

        // Most calls have none: answer that without a locking read.
        await using (var probe = await OpenAsync(ct))
        {
            if (await probe.ExecuteScalarAsync<long>(new CommandDefinition(
                    $"SELECT COUNT(*) FROM work_notices WHERE delivered_at IS NULL AND {where}", parameters, cancellationToken: ct)) == 0)
                return [];
        }

        return await RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var rows = (await connection.QueryAsync<(Guid Id, string BodyJson)>(new CommandDefinition(
                $"SELECT id, body_json FROM work_notices WHERE delivered_at IS NULL AND {where} ORDER BY created_at, id LIMIT 50 FOR UPDATE",
                parameters, transaction, cancellationToken: ct))).ToList();
            if (rows.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_notices SET delivered_at = @now WHERE id IN @ids", new { now, ids = rows.Select(row => row.Id).ToArray() }, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            return (IReadOnlyList<NoticeView>)rows.Select(row => JsonSerializer.Deserialize<NoticeView>(row.BodyJson, WorkJson.Options)!).ToList();
        }, ct);
    }

    // ---- sync_plan ---------------------------------------------------------------------------

    /// <summary>What a preview is bound to: the feature's version, every item in scope and the edges into them.</summary>
    private sealed record PlanVersions(long FeatureVersion, Dictionary<Guid, long> Items, string EdgesHash);

    private sealed record StoredPreview(string Id, Guid ProjectId, Guid FeatureId, string Identity, string PlanJson, string VersionsJson, DateTime ExpiresAt, DateTime? AppliedAt, string? ResultJson);

    private sealed record StoredApplied(Dictionary<string, CreatedRef> Items, List<string>? Added, List<string>? Edited, List<string>? Closed, List<PlanPivot>? Pivots);

    // Stored plan specs are internal: the default options keep them exact.
    private static readonly JsonSerializerOptions StoredPlanOptions = new();

    /// <summary>The items a plan concerns, read in the caller's snapshot (after the apply has held them).</summary>
    private sealed class PlanScope
    {
        public required FeatureRow Feature { get; init; }
        public required List<ItemRow> FeatureItems { get; init; }
        public required Dictionary<Guid, ItemRow> Rows { get; init; }
        public required List<(Guid Blocked, Guid Blocker)> ProjectEdges { get; init; }
        public required PlanVersions Versions { get; init; }
    }

    private static IEnumerable<Guid> NamedIds(RevisedPlanSpec plan) =>
        plan.Items.Select(item => item.KeyMatch).OfType<Guid>()
            .Concat(plan.Items.SelectMany(item => item.BlockedBy ?? []).Select(blocker => blocker.ItemId).OfType<Guid>());

    private static async Task<PlanScope> ReadScopeAsync(MySqlConnection connection, MySqlTransaction transaction, Guid featureId, RevisedPlanSpec plan, CancellationToken ct)
    {
        var feature = await ReadFeatureRowAsync(connection, transaction, featureId, ct)
            ?? throw WorkException.NotFound($"There is no feature {featureId}.");
        var featureItems = (await connection.QueryAsync<ItemRow>(new CommandDefinition(
            $"SELECT {ItemColumns} FROM work_items i JOIN work_projects p ON p.id = i.project_id WHERE i.feature_id = @featureId ORDER BY i.number",
            new { featureId }, transaction, cancellationToken: ct))).ToList();
        var rows = featureItems.ToDictionary(row => row.Id);
        var named = NamedIds(plan).Where(id => !rows.ContainsKey(id)).Distinct().ToArray();
        if (named.Length > 0)
        {
            foreach (var row in await connection.QueryAsync<ItemRow>(new CommandDefinition(
                         $"SELECT {ItemColumns} FROM work_items i JOIN work_projects p ON p.id = i.project_id WHERE i.id IN @named",
                         new { named }, transaction, cancellationToken: ct)))
                rows[row.Id] = row;
        }

        var missing = named.Where(id => !rows.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw WorkException.NotFound($"The plan names {string.Join(", ", missing)}, which {(missing.Count == 1 ? "is not an item" : "are not items")}. Nothing was changed.");
        var foreign = rows.Values.Where(row => row.ProjectId != feature.ProjectId).Select(row => row.Key).ToList();
        if (foreign.Count > 0)
            throw WorkException.Invalid($"The plan names {string.Join(", ", foreign)} of another project: a plan stays within its feature's project. Nothing was changed.");

        var edges = (await connection.QueryAsync<(Guid Blocked, Guid Blocker)>(new CommandDefinition(
            "SELECT d.blocked_item_id, d.blocker_item_id FROM work_item_dependencies d JOIN work_items i ON i.id = d.blocked_item_id WHERE i.project_id = @projectId",
            new { projectId = feature.ProjectId }, transaction, cancellationToken: ct))).ToList();
        var scopeEdges = edges.Where(edge => rows.ContainsKey(edge.Blocked)).Select(edge => $"{edge.Blocked}>{edge.Blocker}").Order(StringComparer.Ordinal);
        var edgesHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", scopeEdges))));
        return new PlanScope
        {
            Feature = feature,
            FeatureItems = featureItems,
            Rows = rows,
            ProjectEdges = edges,
            Versions = new PlanVersions(feature.Version, rows.Values.ToDictionary(row => row.Id, row => row.Version), edgesHash)
        };
    }

    /// <summary>The fields of an item a plan sets, as the diff compares them.</summary>
    private static Dictionary<string, object?> PlanFields(ItemRow row) => new()
    {
        ["title"] = row.Title,
        ["description"] = row.Description,
        ["goal"] = row.Goal,
        ["type"] = ParseName<WorkItemType>(row.ItemType),
        ["status"] = WorkStatuses.Parse(row.Status),
        ["priority"] = ParseName<WorkPriority>(row.Priority),
        ["order"] = row.SortOrder,
        ["labels"] = SplitLabels(row.Labels) is { Count: > 0 } labels ? labels : null,
        ["repository"] = row.RepositoryName,
        ["acceptance_criteria"] = ReadList<AcceptanceCriterionView>(row.AcceptanceJson) is { Count: > 0 } criteria ? criteria : null,
        ["inherits"] = ReadList<string>(row.InheritsJson) is { Count: > 0 } inherits ? inherits : null,
        ["touches"] = ReadList<string>(row.TouchesJson) is { Count: > 0 } touches ? touches : null
    };

    private static Dictionary<string, object?> FeaturePlanFields(FeatureRow row) => new()
    {
        ["title"] = row.Title,
        ["description"] = row.Description,
        ["goals"] = ReadList<string>(row.GoalsJson) is { Count: > 0 } goals ? goals : null,
        ["requirements"] = ReadList<RequirementView>(row.RequirementsJson) is { Count: > 0 } requirements ? requirements : null,
        ["acceptance_tests"] = ReadList<string>(row.AcceptanceTestsJson) is { Count: > 0 } tests ? tests : null
    };

    /// <summary>The diff of a revised plan against the stored feature: computed the same way for the preview and the apply.</summary>
    private sealed class PlanDiff
    {
        public required PlanScope Scope { get; init; }
        public required RevisedPlanSpec Plan { get; init; }
        public required List<RequirementView> RequirementsBefore { get; init; }
        public required List<RequirementView> RequirementsAfter { get; init; }
        public required Dictionary<string, object?> FeatureBefore { get; init; }
        public required Dictionary<string, object?> FeatureAfter { get; init; }
        public required ItemRow?[] Matched { get; init; }
        public required Guid[] Nodes { get; init; }
        public required Dictionary<string, object?>[] After { get; init; }
        public required List<int> Added { get; init; }
        public required List<int> Edited { get; init; }
        public required HashSet<int> Material { get; init; }
        public required List<ItemRow> Closed { get; init; }
        public required List<(Guid Blocked, Guid Blocker)> EdgesAdded { get; init; }
        public required List<(Guid Blocked, Guid Blocker)> EdgesRemoved { get; init; }
        public required Dictionary<Guid, AttemptRow> ActiveAttempts { get; init; }
        public required List<string> Warnings { get; init; }

        public bool FeatureChanged => Diff(FeatureBefore, FeatureAfter).Count > 0;

        public bool Unchanged => !FeatureChanged && Added.Count == 0 && Edited.Count == 0 && Closed.Count == 0;

        public string Label(Guid id)
        {
            var index = Array.IndexOf(Nodes, id);
            if (index >= 0 && Matched[index] is null)
                return $"'{Plan.Items[index].Ref}' (new)";
            return Scope.Rows.TryGetValue(id, out var row) ? row.Key : id.ToString();
        }

        public bool IsPivot(ItemRow row, DateTime now) =>
            ActiveAttempts.ContainsKey(row.Id) || row.HasLiveClaim(now);
    }

    private static async Task<PlanDiff> ComputePlanAsync(MySqlConnection connection, MySqlTransaction transaction, PlanScope scope, RevisedPlanSpec plan, DateTime now, CancellationToken ct)
    {
        var feature = scope.Feature;
        var requirementsBefore = ReadList<RequirementView>(feature.RequirementsJson);
        var requirementsAfter = MergeRequirements(requirementsBefore, plan.Requirements, plan.DropRequirements, feature.Key);
        var featureBefore = FeaturePlanFields(feature);
        var featureAfter = new Dictionary<string, object?>(featureBefore)
        {
            ["title"] = plan.Title ?? feature.Title,
            ["description"] = plan.Description ?? feature.Description,
            ["requirements"] = requirementsAfter.Count == 0 ? null : requirementsAfter
        };
        if (plan.Goals is not null)
            featureAfter["goals"] = plan.Goals.Count == 0 ? null : plan.Goals;
        if (plan.AcceptanceTests is not null)
            featureAfter["acceptance_tests"] = plan.AcceptanceTests.Count == 0 ? null : plan.AcceptanceTests;

        var owned = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT repository_name FROM work_project_repositories WHERE project_id = @projectId AND role = 'owned'",
            new { projectId = feature.ProjectId }, transaction, cancellationToken: ct))).ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);

        // Match each plan item to an item of the feature by ref, or by key (an item of the feature,
        // or of no feature, which the re-plan adopts).
        var byRef = scope.FeatureItems.Where(row => row.PlanRef is not null).ToDictionary(row => row.PlanRef!, StringComparer.Ordinal);
        var matched = new ItemRow?[plan.Items.Count];
        var claimedBy = new Dictionary<Guid, int>();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            var row = byRef.GetValueOrDefault(item.Ref);
            if (row is null && item.KeyMatch is { } keyMatch)
            {
                row = scope.Rows[keyMatch];
                if (row.FeatureId is { } other && other != feature.Id)
                    throw WorkException.Invalid($"plan.items[{index}]: {row.Key} belongs to another feature. Move it to {feature.Key} with update_work first, or leave it out. Nothing was changed.");
            }

            if (row is not null)
            {
                if (claimedBy.TryGetValue(row.Id, out var earlier))
                    throw WorkException.Invalid($"plan.items[{earlier}] and plan.items[{index}] both name {row.Key}. Nothing was changed.");
                claimedBy[row.Id] = index;
            }

            matched[index] = row;
        }

        var nodes = matched.Select(row => row?.Id ?? Guid.NewGuid()).ToArray();
        var after = new Dictionary<string, object?>[plan.Items.Count];
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            var row = matched[index];
            var where = $"plan.items[{index}] ('{item.Ref}')";
            if (row is null && item.Title is null)
                throw WorkException.Invalid($"{where} adds an item to {feature.Key}, so it needs title. Nothing was changed.");

            var fields = row is null
                ? new Dictionary<string, object?>
                {
                    ["title"] = item.Title, ["description"] = null, ["goal"] = null, ["type"] = WorkItemType.Story, ["status"] = WorkStatus.Backlog,
                    ["priority"] = WorkPriority.Medium, ["order"] = 0, ["labels"] = null, ["repository"] = null, ["acceptance_criteria"] = null,
                    ["inherits"] = null, ["touches"] = null
                }
                : PlanFields(row);
            if (item.Title is not null)
                fields["title"] = item.Title;
            if (item.Description is not null)
                fields["description"] = item.Description;
            if (item.Goal is not null)
                fields["goal"] = item.Goal;
            if (item.Type is { } type)
                fields["type"] = type;
            if (item.Status is { } status)
                fields["status"] = status;
            if (item.Priority is { } priority)
                fields["priority"] = priority;
            if (item.Order is { } order)
                fields["order"] = order;
            if (item.Labels is not null)
                fields["labels"] = item.Labels.Count == 0 ? null : item.Labels;
            if (item.Repository is { } repository)
                fields["repository"] = owned.TryGetValue(repository, out var canonical)
                    ? canonical
                    : throw WorkException.Invalid($"{where}: '{repository}' is not an owned repository of {feature.ProjectKey}. Nothing was changed.");
            if (item.AcceptanceCriteria is not null)
            {
                var criteria = NormalizeCriteria(row is null ? [] : ReadList<AcceptanceCriterionView>(row.AcceptanceJson), item.AcceptanceCriteria);
                fields["acceptance_criteria"] = criteria.Count == 0 ? null : criteria;
            }

            if (item.Inherits is not null)
            {
                RequireInherits(item.Inherits, requirementsAfter, $"{where}.inherits");
                fields["inherits"] = item.Inherits.Count == 0 ? null : item.Inherits;
            }
            else if (fields["inherits"] is List<string> { Count: > 0 } kept)
            {
                RequireInherits(kept, requirementsAfter, $"{where} (its inherits)");
            }

            if (item.Touches is not null)
                fields["touches"] = item.Touches.Count == 0 ? null : item.Touches;
            after[index] = fields;
        }

        // Dependencies: blocked_by, when given, is the item's whole blocker list.
        var nodeOfItem = new Dictionary<Guid, Guid>();
        for (var index = 0; index < nodes.Length; index++)
            nodeOfItem[nodes[index]] = nodes[index];
        var added = new List<(Guid Blocked, Guid Blocker)>();
        var removed = new List<(Guid Blocked, Guid Blocker)>();
        var edgesChanged = new HashSet<int>();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            if (plan.Items[index].BlockedBy is not { } blockers)
                continue;
            var desired = blockers.Select(blocker => blocker.PlanIndex is { } other ? nodes[other] : blocker.ItemId!.Value).Distinct().ToList();
            if (desired.Contains(nodes[index]))
                throw new WorkException(WorkErrorCodes.Cycle, $"plan.items[{index}] ('{plan.Items[index].Ref}') would block itself. Nothing was changed.");
            var current = matched[index] is { } row ? scope.ProjectEdges.Where(edge => edge.Blocked == row.Id).Select(edge => edge.Blocker).ToList() : [];
            foreach (var blocker in desired.Except(current))
                added.Add((nodes[index], blocker));
            foreach (var blocker in current.Except(desired))
                removed.Add((nodes[index], blocker));
            if (desired.Except(current).Any() || current.Except(desired).Any())
                edgesChanged.Add(index);
        }

        // The project's graph after the plan, checked for a loop through every added edge.
        var blockersOf = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var (blocked, blocker) in scope.ProjectEdges.Except(removed).Concat(added))
        {
            if (!blockersOf.TryGetValue(blocked, out var set))
                blockersOf[blocked] = set = [];
            set.Add(blocker);
        }

        var labelDiff = new PlanDiff
        {
            Scope = scope, Plan = plan, RequirementsBefore = requirementsBefore, RequirementsAfter = requirementsAfter, FeatureBefore = featureBefore,
            FeatureAfter = featureAfter, Matched = matched, Nodes = nodes, After = after, Added = [], Edited = [], Material = [], Closed = [],
            EdgesAdded = added, EdgesRemoved = removed, ActiveAttempts = [], Warnings = []
        };
        foreach (var (blocked, blocker) in added)
        {
            var seen = new HashSet<Guid> { blocker };
            var pending = new Stack<Guid>([blocker]);
            while (pending.TryPop(out var current))
            {
                foreach (var next in blockersOf.GetValueOrDefault(current) ?? [])
                {
                    if (next == blocked)
                        throw new WorkException(WorkErrorCodes.Cycle,
                            $"{labelDiff.Label(blocked)} blocked by {labelDiff.Label(blocker)} would make an item block itself, directly or through other items. Nothing was changed.");
                    if (seen.Add(next))
                        pending.Push(next);
                }
            }
        }

        var addedItems = new List<int>();
        var edited = new List<int>();
        var material = new HashSet<int>();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            if (matched[index] is not { } row)
            {
                addedItems.Add(index);
                continue;
            }

            var fieldsChanged = Diff(PlanFields(row), after[index]).Count > 0;
            if (fieldsChanged || edgesChanged.Contains(index))
                material.Add(index);
            if (material.Contains(index) || row.FeatureId != feature.Id || row.PlanRef != plan.Items[index].Ref)
                edited.Add(index);
        }

        var closed = scope.FeatureItems
            .Where(row => !claimedBy.ContainsKey(row.Id) && !WorkStatuses.IsFinished(WorkStatuses.Parse(row.Status)))
            .ToList();

        var pivotCandidates = edited.Where(material.Contains).Select(index => matched[index]!.Id).Concat(closed.Select(row => row.Id)).ToArray();
        var attempts = pivotCandidates.Length == 0
            ? []
            : (await connection.QueryAsync<AttemptRow>(new CommandDefinition(
                $"SELECT {AttemptColumns} FROM work_attempts a LEFT JOIN work_sessions s ON s.id = a.session_id WHERE a.item_id IN @ids AND a.outcome = 'active'",
                new { ids = pivotCandidates }, transaction, cancellationToken: ct))).ToDictionary(attempt => attempt.ItemId);

        var diff = new PlanDiff
        {
            Scope = scope, Plan = plan, RequirementsBefore = requirementsBefore, RequirementsAfter = requirementsAfter, FeatureBefore = featureBefore,
            FeatureAfter = featureAfter, Matched = matched, Nodes = nodes, After = after, Added = addedItems, Edited = edited, Material = material,
            Closed = closed, EdgesAdded = added, EdgesRemoved = removed, ActiveAttempts = attempts, Warnings = []
        };

        // Items left blocked by a closed prerequisite after the plan (they show cancelled_prerequisite).
        var closedAfter = new HashSet<Guid>(closed.Select(row => row.Id));
        for (var index = 0; index < plan.Items.Count; index++)
        {
            if (after[index]["status"] is WorkStatus.Closed)
                closedAfter.Add(nodes[index]);
        }

        foreach (var row in scope.Rows.Values.Where(row => row.Status == "closed"))
            closedAfter.Add(row.Id);
        var openPlanned = Enumerable.Range(0, plan.Items.Count).Where(index => !WorkStatuses.IsFinished((WorkStatus)after[index]["status"]!)).ToList();
        foreach (var index in openPlanned)
        {
            foreach (var blocker in blockersOf.GetValueOrDefault(nodes[index]) ?? [])
            {
                if (closedAfter.Contains(blocker))
                    diff.Warnings.Add($"{diff.Label(nodes[index])} is blocked by {diff.Label(blocker)}, which is closed: it shows cancelled_prerequisite until a re-plan replaces that edge.");
            }
        }

        var closedIds = closed.Select(row => row.Id).ToArray();
        if (closedIds.Length > 0)
        {
            var dependents = await connection.QueryAsync<(Guid Blocked, Guid Blocker, string ProjectKey, int Number, string Status)>(new CommandDefinition(
                "SELECT d.blocked_item_id, d.blocker_item_id, p.project_key, b.number, b.status FROM work_item_dependencies d " +
                "JOIN work_items b ON b.id = d.blocked_item_id JOIN work_projects p ON p.id = b.project_id WHERE d.blocker_item_id IN @closedIds",
                new { closedIds }, transaction, cancellationToken: ct));
            foreach (var dependent in dependents.Where(dependent => !nodes.Contains(dependent.Blocked) && dependent.Status is not ("complete" or "closed")))
                diff.Warnings.Add($"{WorkKeys.Item(dependent.ProjectKey, dependent.Number)} is blocked by {diff.Label(dependent.Blocker)}, which the plan closes: it shows cancelled_prerequisite until a re-plan replaces that edge.");
        }

        return diff;
    }

    private static PlanPivot Pivot(PlanDiff diff, ItemRow row, string action, DateTime now)
    {
        if (diff.ActiveAttempts.TryGetValue(row.Id, out var attempt))
            return new PlanPivot(row.Key, action, new HolderView(attempt.Holder, attempt.SessionId, attempt.Harness, attempt.Model, attempt.Host), attempt.Id);
        return new PlanPivot(row.Key, action, row.HasLiveClaim(now) ? new HolderView(row.ClaimHolder!, row.ClaimSessionId) : null);
    }

    private static List<PlanPivot> Pivots(PlanDiff diff, DateTime now) =>
        diff.Edited.Where(diff.Material.Contains).Select(index => diff.Matched[index]!).Where(row => diff.IsPivot(row, now)).Select(row => Pivot(diff, row, "edit", now))
            .Concat(diff.Closed.Where(row => diff.IsPivot(row, now)).Select(row => Pivot(diff, row, "close", now)))
            .ToList();

    private static SyncPlanPreview ToPreview(PlanDiff diff, string token, DateTime expiresAt, DateTime now)
    {
        var featureChanges = Diff(diff.FeatureBefore, diff.FeatureAfter);
        var before = diff.RequirementsBefore.ToDictionary(requirement => requirement.Id);
        var added = diff.RequirementsAfter.Where(requirement => requirement.State == RequirementState.Active && !before.ContainsKey(requirement.Id)).Select(requirement => requirement.Id).ToList();
        var changed = diff.RequirementsAfter.Where(requirement => requirement.State == RequirementState.Active && before.TryGetValue(requirement.Id, out var old) &&
            (old.Text != requirement.Text || old.State != RequirementState.Active)).Select(requirement => requirement.Id).ToList();
        var dropped = diff.RequirementsAfter.Where(requirement => requirement.State == RequirementState.Dropped && before.TryGetValue(requirement.Id, out var old) &&
            old.State == RequirementState.Active).Select(requirement => requirement.Id).ToList();

        var itemsAdded = diff.Added.Select(index => new PlanItemChange(diff.Plan.Items[index].Ref, null, (string)diff.After[index]["title"]!)).ToList();
        var itemsEdited = diff.Edited.Select(index =>
        {
            var row = diff.Matched[index]!;
            var changes = Diff(PlanFields(row), diff.After[index]);
            if (row.FeatureId != diff.Scope.Feature.Id)
                changes.Add(Change("feature", null, diff.Scope.Feature.Key));
            if (row.PlanRef != diff.Plan.Items[index].Ref)
                changes.Add(Change("ref", row.PlanRef, diff.Plan.Items[index].Ref));
            return new PlanItemChange(diff.Plan.Items[index].Ref, row.Key, (string)diff.After[index]["title"]!,
                changes.Count == 0 ? null : JsonSerializer.SerializeToElement(changes, WorkJson.Options),
                diff.Material.Contains(index) && diff.IsPivot(row, now) ? true : null);
        }).ToList();
        var itemsClosed = diff.Closed.Select(row => new PlanItemChange(row.PlanRef ?? row.Key, row.Key, row.Title, null, diff.IsPivot(row, now) ? true : null)).ToList();
        var pivots = Pivots(diff, now);

        return new SyncPlanPreview(
            token, Utc(expiresAt), diff.Scope.Feature.Key, diff.Scope.Feature.Version, diff.Unchanged,
            featureChanges.Count == 0 ? null : JsonSerializer.SerializeToElement(featureChanges, WorkJson.Options),
            NullIfEmpty(added), NullIfEmpty(changed), NullIfEmpty(dropped),
            NullIfEmpty(itemsAdded), NullIfEmpty(itemsEdited), NullIfEmpty(itemsClosed),
            NullIfEmpty(diff.EdgesAdded.Select(edge => new PlanDependencyChange(diff.Label(edge.Blocked), diff.Label(edge.Blocker))).ToList()),
            NullIfEmpty(diff.EdgesRemoved.Select(edge => new PlanDependencyChange(diff.Label(edge.Blocked), diff.Label(edge.Blocker))).ToList()),
            NullIfEmpty(pivots), NullIfEmpty(diff.Warnings));
    }

    private static IReadOnlyList<T>? NullIfEmpty<T>(List<T> values) => values.Count == 0 ? null : values;

    public Task<SyncPlanPreview> PreviewPlanAsync(Guid featureId, RevisedPlanSpec plan, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            // One snapshot: the versions the token binds are the state the diff was computed from.
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var scope = await ReadScopeAsync(connection, transaction, featureId, plan, ct);
            var diff = await ComputePlanAsync(connection, transaction, scope, plan, now, ct);
            var id = Guid.NewGuid();
            var expiresAt = now.AddSeconds(WorkLimits.PlanPreviewSeconds);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_plan_previews (id, project_id, feature_id, identity, plan_json, versions_json, created_at, expires_at) " +
                "VALUES (@id, @projectId, @featureId, @identity, @plan, @versions, @now, @expiresAt)",
                new
                {
                    id, projectId = scope.Feature.ProjectId, featureId, identity = actor.Name,
                    plan = JsonSerializer.Serialize(plan, StoredPlanOptions), versions = JsonSerializer.Serialize(scope.Versions, StoredPlanOptions), now, expiresAt
                },
                transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            return ToPreview(diff, id.ToString(), expiresAt, now);
        }, ct);

    private sealed class PreviewRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public Guid FeatureId { get; init; }
        public string Identity { get; init; } = "";
        public string PlanJson { get; init; } = "";
        public string VersionsJson { get; init; } = "";
        public DateTime ExpiresAt { get; init; }
        public DateTime? AppliedAt { get; init; }
        public string? ResultJson { get; init; }
    }

    public Task<SyncPlanApplied> ApplyPlanAsync(Guid previewId, Guid? projectId, WorkActor actor, CancellationToken ct = default) =>
        // Plans add dependencies, so applies run under the dependency lock like every edge add: one
        // at a time, and the cycle check sees every edge committed before it.
        RunAsync([DependencyLockName], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);

            // Every row the apply depends on is held before anything is read without a lock, so the
            // snapshot the reads share starts after the holds: the preview, the project (item
            // numbers), the feature, the feature's items and the items the plan names.
            var preview = await connection.QuerySingleOrDefaultAsync<PreviewRow>(new CommandDefinition(
                "SELECT id, project_id, feature_id, identity, plan_json, versions_json, expires_at, applied_at, result_json FROM work_plan_previews WHERE id = @previewId FOR UPDATE",
                new { previewId }, transaction, cancellationToken: ct));
            if (preview is null || preview.Identity != actor.Name)
                throw WorkException.NotFound($"There is no plan preview {previewId} of yours. Ask sync_plan for a preview with plan.");
            if (projectId is { } sessionProject && preview.ProjectId != sessionProject)
                throw WorkException.Invalid($"Plan preview {previewId} is of another project than the session's. Nothing was changed.");

            if (preview.AppliedAt is not null)
            {
                // A repeat (say, after a lost answer): the first apply's answer, nothing written again.
                var stored = JsonSerializer.Deserialize<StoredApplied>(preview.ResultJson!, StoredPlanOptions)!;
                var current = (await LoadFeatureViewAsync(connection, transaction, preview.FeatureId, withCounts: true, ct))!;
                await transaction.CommitAsync(ct);
                return new SyncPlanApplied(true, current, stored.Items, stored.Added, stored.Edited, stored.Closed, stored.Pivots);
            }

            if (preview.ExpiresAt <= now)
                throw new WorkException(WorkErrorCodes.Conflict, $"Plan preview {previewId} expired at {Utc(preview.ExpiresAt):O}. Nothing was changed; ask sync_plan for a new preview.");

            var plan = JsonSerializer.Deserialize<RevisedPlanSpec>(preview.PlanJson, StoredPlanOptions)!;
            var bound = JsonSerializer.Deserialize<PlanVersions>(preview.VersionsJson, StoredPlanOptions)!;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_projects SET version = version WHERE id = @projectId", new { projectId = preview.ProjectId }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT id FROM work_features WHERE id = @featureId FOR UPDATE", new { featureId = preview.FeatureId }, transaction, cancellationToken: ct));
            var held = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM work_items WHERE feature_id = @featureId FOR UPDATE", new { featureId = preview.FeatureId }, transaction, cancellationToken: ct))).ToList();
            var others = bound.Items.Keys.Concat(NamedIds(plan)).Except(held).Distinct().Order().ToArray();
            if (others.Length > 0)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET version = version WHERE id IN @others", new { others }, transaction, cancellationToken: ct));

            var scope = await ReadScopeAsync(connection, transaction, preview.FeatureId, plan, ct);
            var same = scope.Versions.FeatureVersion == bound.FeatureVersion &&
                scope.Versions.EdgesHash == bound.EdgesHash &&
                scope.Versions.Items.Count == bound.Items.Count &&
                scope.Versions.Items.All(entry => bound.Items.TryGetValue(entry.Key, out var version) && version == entry.Value);
            if (!same)
            {
                var feature = await LoadFeatureViewAsync(connection, transaction, preview.FeatureId, withCounts: false, ct);
                throw new WorkException(WorkErrorCodes.Conflict,
                    $"Plan preview {previewId} is stale: {feature?.Key ?? "the feature"}, its items or their dependencies changed after the preview. " +
                    "Nothing was changed; ask sync_plan for a new preview of the revised plan and confirm that.", feature);
            }

            var diff = await ComputePlanAsync(connection, transaction, scope, plan, now, ct);
            var (result, entries) = diff.Unchanged
                ? (new StoredApplied(PlanRefs(diff, null), null, null, null, null), new List<PendingChange>())
                : await WritePlanAsync(connection, transaction, diff, previewId, actor, now, ct);

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_plan_previews SET applied_at = @now, result_json = @result WHERE id = @previewId",
                new { now, result = JsonSerializer.Serialize(result, StoredPlanOptions), previewId }, transaction, cancellationToken: ct));
            var view = (await LoadFeatureViewAsync(connection, transaction, preview.FeatureId, withCounts: true, ct))!;
            await CommitAsync(connection, transaction, entries, actor, now, ct);
            return new SyncPlanApplied(false, view, result.Items, result.Added, result.Edited, result.Closed, result.Pivots);
        }, ct);

    /// <summary>
    /// Writes a computed diff in the apply's transaction and answers what it did and the change-feed
    /// entries: the feature first, then the items it adds, then the items it edits or closes. An
    /// edited or closed item that is in progress or claimed has its current attempt superseded and
    /// its holder told (comment on the item, changed_by_replan notice); an edit keeps the claim,
    /// under a new attempt, while a close ends it.
    /// </summary>
    private async Task<(StoredApplied Result, List<PendingChange> Entries)> WritePlanAsync(
        MySqlConnection connection, MySqlTransaction transaction, PlanDiff diff, Guid previewId, WorkActor actor, DateTime now, CancellationToken ct)
    {
        var feature = diff.Scope.Feature;
        var project = (await ReadProjectRowAsync(connection, transaction, feature.ProjectId, ct))!;
        var entries = new List<PendingChange>();

        // Before-views of the items the plan changes, for their feed entries.
        var beforeViews = new Dictionary<Guid, (ItemView View, ItemRow Row)>();
        foreach (var row in diff.Edited.Select(index => diff.Matched[index]!).Concat(diff.Closed))
            beforeViews[row.Id] = (await ToViewAsync(connection, transaction, row, ct), row);
        var featureBefore = (await LoadFeatureViewAsync(connection, transaction, feature.Id, withCounts: false, ct))!;

        // The feature: its fields, and its version advanced by every applied re-plan.
        var after = diff.FeatureAfter;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_features SET title = @title, description = @description, goals_json = @goals, requirements_json = @requirements, acceptance_tests_json = @tests, " +
            "version = version + 1, updated_at = @now WHERE id = @id",
            new
            {
                title = (string)after["title"]!, description = (string?)after["description"], goals = WriteList(after["goals"] as IReadOnlyList<string>),
                requirements = WriteList(diff.RequirementsAfter), tests = WriteList(after["acceptance_tests"] as IReadOnlyList<string>), now, id = feature.Id
            },
            transaction, cancellationToken: ct));

        // New items, numbered in plan order after the project's last item.
        var created = new Dictionary<int, CreatedRef>();
        for (var position = 0; position < diff.Added.Count; position++)
        {
            var index = diff.Added[position];
            var fields = diff.After[index];
            var item = diff.Plan.Items[index];
            var number = project.NextItemNumber + position;
            await InsertItemAsync(connection, transaction, project.Id, diff.Nodes[index], number, (string)fields["title"]!, (WorkItemType)fields["type"]!,
                (WorkStatus)fields["status"]!, (WorkPriority)fields["priority"]!, (string?)fields["description"], (fields["labels"] as IReadOnlyList<string>) ?? [],
                (string?)fields["repository"], feature.Id, (int)fields["order"]!, actor, now.AddTicks(position * 10), ct,
                new ItemPlanFields((string?)fields["goal"], null, fields["inherits"] as IReadOnlyList<string>, fields["touches"] as IReadOnlyList<string>, item.Ref));
            if (fields["acceptance_criteria"] is IReadOnlyList<AcceptanceCriterionView> criteria)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET acceptance_json = @acceptance WHERE id = @id", new { acceptance = WriteList(criteria), id = diff.Nodes[index] }, transaction, cancellationToken: ct));
            created[index] = new CreatedRef(diff.Nodes[index], WorkKeys.Item(project.ProjectKey, number));
        }

        if (diff.Added.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_projects SET next_item_number = next_item_number + @count WHERE id = @projectId",
                new { count = diff.Added.Count, projectId = project.Id }, transaction, cancellationToken: ct));

        // Dependencies: removed, then added (every node exists now).
        foreach (var (blocked, blocker) in diff.EdgesRemoved)
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM work_item_dependencies WHERE blocked_item_id = @blocked AND blocker_item_id = @blocker", new { blocked, blocker }, transaction, cancellationToken: ct));
        await InsertEdgesAsync(connection, transaction, diff.EdgesAdded, now, ct);

        var summary = $"Re-planned by {actor.Name} (sync_plan preview {previewId})";
        var pivots = new List<PlanPivot>();
        var extras = new Dictionary<Guid, JsonArray>();
        JsonArray ExtraOf(Guid id) => extras.TryGetValue(id, out var extra) ? extra : extras[id] = [];

        foreach (var (blocked, blocker) in diff.EdgesRemoved)
            ExtraOf(blocked).Add(Change("blocked_by", diff.Label(blocker), null));
        foreach (var (blocked, blocker) in diff.EdgesAdded.Where(edge => diff.Matched.Any(row => row?.Id == edge.Blocked)))
            ExtraOf(blocked).Add(Change("blocked_by", null, created.Values.FirstOrDefault(entry => entry.Id == blocker)?.Key ?? diff.Label(blocker)));

        // Edited items.
        foreach (var index in diff.Edited)
        {
            var row = diff.Matched[index]!;
            var fields = diff.After[index];
            var status = (WorkStatus)fields["status"]!;
            var closes = status == WorkStatus.Closed && row.Status != "closed";
            var changedFields = Diff(PlanFields(row), fields).Select(change => (string)change!["field"]!).ToList();
            if (diff.Material.Contains(index) && changedFields.Count == 0)
                changedFields.Add("blocked_by");
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET title = @title, description = @description, goal = @goal, item_type = @type, status = @status, priority = @priority, " +
                "sort_order = @order, labels = @labels, repository_name = @repository, acceptance_json = @acceptance, inherits_json = @inherits, touches_json = @touches, " +
                "feature_id = @featureId, plan_ref = @planRef, version = version + 1, updated_at = @now WHERE id = @id",
                new
                {
                    title = (string)fields["title"]!, description = (string?)fields["description"], goal = (string?)fields["goal"],
                    type = JsonName((WorkItemType)fields["type"]!), status = WorkStatuses.Name(status), priority = JsonName((WorkPriority)fields["priority"]!),
                    order = (int)fields["order"]!, labels = JoinLabels((fields["labels"] as IReadOnlyList<string>) ?? []), repository = (string?)fields["repository"],
                    acceptance = WriteList(fields["acceptance_criteria"] as IReadOnlyList<AcceptanceCriterionView>),
                    inherits = WriteList(fields["inherits"] as IReadOnlyList<string>), touches = WriteList(fields["touches"] as IReadOnlyList<string>),
                    featureId = feature.Id, planRef = diff.Plan.Items[index].Ref, now, id = row.Id
                },
                transaction, cancellationToken: ct));

            if (closes)
                await CloseByPlanAsync(connection, transaction, diff, row, summary, actor, now, pivots, ExtraOf(row.Id), ct);
            else if (diff.Material.Contains(index) && diff.IsPivot(row, now))
                await PivotAsync(connection, transaction, diff, row, $"{summary}: changed {string.Join(", ", changedFields)}.", changedFields, actor, now, pivots, ExtraOf(row.Id), ct);
        }

        // Items of the feature the plan leaves out.
        foreach (var row in diff.Closed)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET status = 'closed', version = version + 1, updated_at = @now WHERE id = @id", new { now, id = row.Id }, transaction, cancellationToken: ct));
            await CloseByPlanAsync(connection, transaction, diff, row, summary, actor, now, pivots, ExtraOf(row.Id), ct);
        }

        // The feed: the feature, the new items, then the changed ones.
        var featureAfter = (await LoadFeatureViewAsync(connection, transaction, feature.Id, withCounts: false, ct))!;
        var beforeSnapshot = Snapshot(featureBefore);
        var featureChanges = Diff(beforeSnapshot, Snapshot(featureAfter));
        featureChanges.Add(Change("replan", null, new
        {
            preview = previewId,
            added = diff.Added.Count,
            edited = diff.Edited.Count,
            closed = diff.Closed.Count + diff.Edited.Count(index => diff.After[index]["status"] is WorkStatus.Closed && diff.Matched[index]!.Status != "closed")
        }));
        entries.Add(new PendingChange(featureAfter.ProjectId, featureAfter.ProjectKey, WorkEntityKind.Feature, featureAfter.Id, featureAfter.Key, WorkChangeKinds.FeatureUpdated, featureChanges));
        foreach (var index in diff.Added)
            entries.Add(ItemCreatedEntry((await LoadItemViewAsync(connection, transaction, diff.Nodes[index], ct))!));
        foreach (var (id, (beforeView, beforeRow)) in beforeViews)
        {
            var afterRow = (await ReadItemRowAsync(connection, transaction, id, ct))!;
            var afterView = await ToViewAsync(connection, transaction, afterRow, ct);
            var changes = Diff(Snapshot(beforeView), Snapshot(afterView));
            foreach (var change in Diff(ClaimSnapshot(beforeRow), ClaimSnapshot(afterRow)))
                changes.Add(change!.DeepClone());
            foreach (var change in extras.GetValueOrDefault(id) ?? [])
                changes.Add(change!.DeepClone());
            entries.Add(new PendingChange(afterView.ProjectId, afterView.ProjectKey, WorkEntityKind.Item, afterView.Id, afterView.Key, WorkChangeKinds.ItemUpdated, changes));
        }

        var closedKeys = diff.Closed.Select(row => row.Key)
            .Concat(diff.Edited.Where(index => diff.After[index]["status"] is WorkStatus.Closed && diff.Matched[index]!.Status != "closed").Select(index => diff.Matched[index]!.Key))
            .ToList();
        var result = new StoredApplied(
            PlanRefs(diff, created),
            diff.Added.Count == 0 ? null : diff.Added.Select(index => created[index].Key).ToList(),
            diff.Edited.Count == 0 ? null : diff.Edited.Select(index => diff.Matched[index]!.Key).ToList(),
            closedKeys.Count == 0 ? null : closedKeys,
            pivots.Count == 0 ? null : pivots);
        return (result, entries);
    }

    /// <summary>
    /// An in-progress or claimed item a re-plan edits: its attempt is superseded, the claim continues
    /// under a new attempt of the same holder (whose later evidence then counts), and the holder is
    /// told on the item thread and by a notice on its next work-tool response.
    /// </summary>
    private async Task PivotAsync(MySqlConnection connection, MySqlTransaction transaction, PlanDiff diff, ItemRow row, string summary, IReadOnlyList<string> changedFields,
        WorkActor actor, DateTime now, List<PlanPivot> pivots, JsonArray extra, CancellationToken ct)
    {
        var pivot = Pivot(diff, row, "edit", now);
        pivots.Add(pivot);
        if (diff.ActiveAttempts.TryGetValue(row.Id, out var attempt))
        {
            await EndAttemptAsync(connection, transaction, attempt, AttemptOutcome.Superseded, summary, now, extra, ct);
            if (row.HasLiveClaim(now))
            {
                var holder = new WorkActor(row.ClaimHolder!, row.ClaimSessionId is null ? WorkActor.UserKind : WorkActor.SessionKind, row.ClaimSessionId);
                await BeginAttemptAsync(connection, transaction, row, holder, row.ClaimMarker!, now, extra, ct);
            }
        }

        var attemptText = attempt is null ? string.Empty : $" Attempt #{attempt.Number} is superseded; evidence from it no longer moves the item.";
        await AddCommentAsync(connection, transaction, row.Id, $"{summary}{attemptText}", actor, extra, now, ct);
        await NotifyAsync(connection, transaction, diff, row, pivot, summary, closed: false, attempt, changedFields, actor, now, ct);
    }

    /// <summary>
    /// An item a re-plan closes: its claim ends (every session's claim on it is released), its
    /// attempt is superseded keeping its handoff, and its holder is told.
    /// </summary>
    private async Task CloseByPlanAsync(MySqlConnection connection, MySqlTransaction transaction, PlanDiff diff, ItemRow row, string summary,
        WorkActor actor, DateTime now, List<PlanPivot> pivots, JsonArray extra, CancellationToken ct)
    {
        var pivoted = diff.IsPivot(row, now);
        var pivot = Pivot(diff, row, "close", now);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_items SET claim_holder = NULL, claim_marker = NULL, claim_session_id = NULL, claimed_at = NULL, claim_expires_at = NULL WHERE id = @id",
            new { id = row.Id }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_session_claims SET released_at = @now WHERE item_id = @id AND released_at IS NULL", new { now, id = row.Id }, transaction, cancellationToken: ct));

        var attempt = diff.ActiveAttempts.GetValueOrDefault(row.Id) ?? await ActiveAttemptAsync(connection, transaction, row.Id, ct);
        if (attempt is not null)
            await EndAttemptAsync(connection, transaction, attempt, AttemptOutcome.Superseded, $"{summary}: the item is closed.", now, extra, ct);

        var text = $"{summary}: the item is left out of the plan and closed." +
            (attempt is null ? string.Empty : $" Attempt #{attempt.Number} is superseded and its claim ended; its handoff is kept with the attempt.");
        await AddCommentAsync(connection, transaction, row.Id, text, actor, extra, now, ct);
        if (pivoted)
        {
            pivots.Add(pivot);
            await NotifyAsync(connection, transaction, diff, row, pivot, text, closed: true, attempt, ["status"], actor, now, ct);
        }
    }

    private static async Task NotifyAsync(MySqlConnection connection, MySqlTransaction transaction, PlanDiff diff, ItemRow row, PlanPivot pivot, string summary, bool closed,
        AttemptRow? attempt, IReadOnlyList<string> changedFields, WorkActor actor, DateTime now, CancellationToken ct)
    {
        if (pivot.Holder is not { } holder)
            return;
        var notice = new NoticeView(
            WorkNoticeKinds.ChangedByReplan, row.Id, row.Key, actor.Name, summary, now, diff.Scope.Feature.Key, closed ? true : null, attempt?.Id,
            JsonSerializer.SerializeToElement(changedFields, WorkJson.Options));
        await InsertNoticeAsync(connection, transaction, notice, holder.SessionId, holder.Identity, ct);
    }

    private static Dictionary<string, CreatedRef> PlanRefs(PlanDiff diff, IReadOnlyDictionary<int, CreatedRef>? created)
    {
        var refs = new Dictionary<string, CreatedRef>(StringComparer.Ordinal);
        for (var index = 0; index < diff.Plan.Items.Count; index++)
        {
            refs[diff.Plan.Items[index].Ref] = diff.Matched[index] is { } row
                ? new CreatedRef(row.Id, row.Key)
                : created![index];
        }

        return refs;
    }
}
