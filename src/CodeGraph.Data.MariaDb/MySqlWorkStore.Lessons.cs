using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Lessons, rules and challenges (sc-27873, design §5.7). A lesson is a knowledge entry of kind
// lesson; this adds its correction events (recurrence by distinct source event), its rule state,
// contradiction links, challenges, addressed_by links, the served-rule log and the review queue.
//
// Nothing here is decided by a model (E5): a correction merges into an existing lesson only when the
// recording agent says same_as, or when its normalized summary is exactly an active lesson's with an
// overlapping applies-when; similarity (LessonSimilarity, lexical) only lists candidates. Nothing
// here blocks work (E6): a challenged rule stays active, a proposal or contradiction just waits in
// the review queue, and anyone (agent or person) may approve, reject or resolve.
//
// Lock order (the store's: project, feature, items, resources and holds, knowledge rows, change
// feed): every lesson write holds the project row first (save points already do), then knowledge
// rows (two at once in ascending id order), then the lesson tables. The served-rule log is written
// by briefings outside any of that and takes no entry lock.
public sealed partial class MySqlWorkStore
{
    private const string ChallengeOperation = "lesson_challenge";

    private const string NotAddressed = "NOT EXISTS (SELECT 1 FROM lesson_addressed_by ab WHERE ab.entry_id = e.id)";

    private sealed class EventRow
    {
        public Guid CorrectionId { get; init; }
        public Guid EntryId { get; init; }
        public string EventHash { get; init; } = "";
        public bool Counted { get; init; }
        public bool RuleAtEvent { get; init; }
        public bool? Served { get; init; }
        public string? SourceKind { get; init; }
        public string? SourceRef { get; init; }
        public string Summary { get; init; } = "";
        public string? What { get; init; }
        public string? SessionId { get; init; }
        public string Author { get; init; } = "";
        public string AuthorKind { get; init; } = "";
        public DateTime CreatedAt { get; init; }
    }

    private sealed class ChallengeRow
    {
        public Guid Id { get; init; }
        public Guid EntryId { get; init; }
        public Guid ProjectId { get; init; }
        public string Status { get; init; } = "";
        public string Reason { get; init; } = "";
        public string EvidenceJson { get; init; } = "[]";
        public string? ResolutionReason { get; init; }
        public string Author { get; init; } = "";
        public string AuthorKind { get; init; } = "";
        public string? AuthorSessionId { get; init; }
        public string? AuthorHarness { get; init; }
        public string? ResolvedBy { get; init; }
        public string? ResolvedByKind { get; init; }
        public string? ResolvedBySessionId { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ResolvedAt { get; init; }
    }

    private const string ChallengeColumns =
        "c.id, c.entry_id, c.project_id, c.status, c.reason, c.evidence_json, c.resolution_reason, c.author, c.author_kind, c.author_session_id, " +
        "c.author_harness, c.resolved_by, c.resolved_by_kind, c.resolved_by_session_id, c.created_at, c.resolved_at";

    private static ChallengeView ToView(ChallengeRow row) =>
        new(row.Id, row.EntryId, ParseName<ChallengeStatus>(row.Status), row.Reason,
            JsonSerializer.Deserialize<List<EvidenceView>>(row.EvidenceJson, WorkJson.Options) ?? [],
            new WorkActor(row.Author, row.AuthorKind, row.AuthorSessionId, row.AuthorHarness), Utc(row.CreatedAt),
            row.ResolutionReason,
            row.ResolvedBy is { } by ? new WorkActor(by, row.ResolvedByKind ?? WorkActor.UserKind, row.ResolvedBySessionId) : null,
            row.ResolvedAt is { } at ? Utc(at) : null);

    // ---- Correction events --------------------------------------------------------------------

    // The source event of a correction: its ref; else the session that recorded it and the item it
    // is about (a correction reported in update_work and again in end_session on the same item is one
    // event; the same lesson learned again on another item is another); else the correction itself.
    private static string EventKey(KnowledgeSpec spec, WorkActor actor, Guid? item) =>
        spec.Details?.Source?.Ref is { Length: > 0 } reference ? "ref:" + reference.Trim().ToLowerInvariant()
        : actor.SessionId is { Length: > 0 } session ? $"session:{session}:item:{(item is { } id ? id.ToString() : "none")}"
        : "correction:" + spec.Id;

    /// <summary>
    /// The applies-when of a lesson that absorbs a correction (B1): activities are triggers, so they
    /// are united; a location dimension (repositories, paths, tools, harness) left open by either side
    /// stays open, else the values are united.
    /// </summary>
    private static AppliesWhenView? UnionAppliesWhen(AppliesWhenView? a, AppliesWhenView? b)
    {
        static IReadOnlyList<string>? Open(IReadOnlyList<string>? x, IReadOnlyList<string>? y, StringComparer comparer) =>
            x is not { Count: > 0 } || y is not { Count: > 0 } ? null : x.Union(y, comparer).ToList();

        var activity = (a?.Activity ?? []).Union(b?.Activity ?? []).ToList();
        var view = new AppliesWhenView(
            activity.Count == 0 ? null : activity,
            Open(a?.Repositories, b?.Repositories, StringComparer.OrdinalIgnoreCase),
            Open(a?.Paths, b?.Paths, StringComparer.Ordinal),
            Open(a?.Tools, b?.Tools, StringComparer.Ordinal),
            a?.Harness is { } left && b?.Harness is { } right && string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ? left : null);
        return view == new AppliesWhenView() ? null : view;
    }

    private static string AppliesWhenJson(AppliesWhenView? view) => JsonSerializer.Serialize(view, WorkJson.Options);

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool HasActivity(KnowledgeDetails? details) => details?.AppliesWhen?.Activity is { Count: > 0 };

    private static string BestPriority(string? a, string? b) =>
        LessonSimilarity.PriorityRank(a) <= LessonSimilarity.PriorityRank(b) ? a ?? "normal" : b ?? "normal";

    /// <summary>The lesson a new correction merges into: its same_as, else an exact normalized match of overlapping applies-when.</summary>
    private static async Task<Guid?> LessonMergeTargetAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, KnowledgeSpec spec, CancellationToken ct)
    {
        if (spec.SameAs is { } sameAs)
        {
            await RequireLessonAsync(connection, transaction, project.Id, sameAs, "same_as", ct);
            return sameAs;
        }

        // An agent that says contradicts has decided: never merged.
        if (spec.Contradicts is not null)
            return null;

        var matches = (await connection.QueryAsync<(Guid Id, string? DetailsJson)>(new CommandDefinition(
            "SELECT id, details_json FROM knowledge_entries WHERE project_id = @projectId AND kind = 'lesson' AND status = 'active' AND summary_hash = @hash ORDER BY created_at, id",
            new { projectId = project.Id, hash = KnowledgePaths.HashSummary(spec.Summary) }, transaction, cancellationToken: ct))).ToList();
        foreach (var match in matches)
        {
            var details = match.DetailsJson is { } json ? JsonSerializer.Deserialize<KnowledgeDetails>(json, WorkJson.Options) : null;
            if (Overlaps(spec.Details?.AppliesWhen, details?.AppliesWhen))
                return match.Id;
        }

        return null;
    }

    // Two applies-when overlap when each dimension either is left open by one of them or shares a value.
    private static bool Overlaps(AppliesWhenView? a, AppliesWhenView? b)
    {
        static bool Shared<T>(IReadOnlyList<T>? x, IReadOnlyList<T>? y, IEqualityComparer<T>? comparer = null) =>
            x is not { Count: > 0 } || y is not { Count: > 0 } || x.Intersect(y, comparer).Any();

        return Shared(a?.Activity, b?.Activity) &&
               Shared(a?.Repositories, b?.Repositories, StringComparer.OrdinalIgnoreCase) &&
               (a?.Harness is null || b?.Harness is null || string.Equals(a.Harness, b.Harness, StringComparison.OrdinalIgnoreCase)) &&
               (a?.Paths is not { Count: > 0 } || b?.Paths is not { Count: > 0 } ||
                a.Paths.Any(left => b.Paths.Any(right => KnowledgePaths.Covers(left, right) || KnowledgePaths.Covers(right, left))));
    }

    /// <summary>An active lesson of the project, held (FOR UPDATE). Refused not_found, or invalid_argument when it is not one.</summary>
    private static async Task<KnowledgeEntryRow> RequireLessonAsync(MySqlConnection connection, MySqlTransaction transaction, Guid projectId, Guid entryId, string argument, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.id = @entryId FOR UPDATE", new { entryId }, transaction, cancellationToken: ct))
            ?? throw WorkException.NotFound($"{argument}: there is no knowledge entry {entryId}. Nothing was changed.");
        if (row.ProjectId != projectId)
            throw WorkException.Invalid($"{argument}: entry {entryId} belongs to another project; lessons relate only within their project. Nothing was changed.");
        if (row.Kind != "lesson")
            throw WorkException.Invalid($"{argument}: entry {entryId} is a {row.Kind}, not a lesson. Nothing was changed.");
        if (row.Status != "active")
            throw WorkException.Invalid($"{argument}: lesson {entryId} is {row.Status}; only an active lesson can be named. Nothing was changed.");
        return row;
    }

    private static Task InsertEventAsync(MySqlConnection connection, MySqlTransaction transaction, Guid projectId, Guid entryId, KnowledgeSpec spec,
        string key, bool counted, bool ruleAtEvent, bool? served, Guid? item, WorkActor actor, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO lesson_events (correction_id, entry_id, project_id, event_key, event_hash, counted, rule_at_event, served, source_kind, source_ref, " +
            "summary, summary_hash, what, item_id, session_id, author, author_kind, created_at) VALUES (@correction, @entryId, @projectId, @key, @hash, @counted, " +
            "@ruleAtEvent, @served, @sourceKind, @sourceRef, @summary, @summaryHash, @what, @item, @session, @author, @authorKind, @now)",
            new
            {
                correction = spec.Id, entryId, projectId, key = key.Length > 600 ? key[..600] : key, hash = Sha(key), counted, ruleAtEvent, served,
                sourceKind = spec.Details?.Source is { } source ? JsonName(source.Kind) : null, sourceRef = spec.Details?.Source?.Ref,
                summary = spec.Summary, summaryHash = KnowledgePaths.HashSummary(spec.Summary), what = spec.Details?.What, item,
                session = actor.SessionId, author = actor.Name, authorKind = actor.Kind, now
            },
            transaction, cancellationToken: ct));

    /// <summary>The learning-loop state of a newly stored lesson: its first event, rule at once from a prompt, its contradiction link.</summary>
    private async Task InitLessonAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, KnowledgeSpec spec, Guid? item, WorkActor actor, DateTime now, CancellationToken ct)
    {
        var prompt = spec.Details?.Source?.Kind == CorrectionSourceKind.Prompt;
        // A rule needs an applies-when activity: without one it would be served everywhere (§5.7).
        var rule = HasActivity(spec.Details) && (prompt || ruleThreshold <= 1);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET rule_state = @state, rule_reason = @reason, rule_since = @since, recurrence = 1, priority = @priority WHERE id = @id",
            new
            {
                state = rule ? "rule" : "candidate", reason = rule ? JsonName(prompt ? RuleReason.HumanPrompt : RuleReason.Recurrence) : null,
                since = rule ? now : (DateTime?)null, priority = LessonSimilarity.Priority(spec.Details?.Source?.Kind), id = spec.Id
            },
            transaction, cancellationToken: ct));
        await InsertEventAsync(connection, transaction, project.Id, spec.Id, spec, EventKey(spec, actor, item), counted: true, ruleAtEvent: false, served: null, item, actor, now, ct);

        if (spec.Contradicts is { } other)
        {
            var target = await RequireLessonAsync(connection, transaction, project.Id, other, "contradicts", ct);
            await LinkContradictionAsync(connection, transaction, spec.Id, other, actor, now, ct);
            await BumpLessonAsync(connection, transaction, target, "contradicted", actor, now, ct);
        }
    }

    /// <summary>
    /// A correction merged into <paramref name="target"/>: an event; the correction's applies-when
    /// united into the lesson's; and when it is a new event a recurrence. Promotion is evaluated after
    /// both, so a prompt correction with an activity merged into a lesson without one makes it a rule at once (R24).
    /// </summary>
    private async Task AddLessonEventAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, Guid target, KnowledgeSpec spec, Guid? item,
        WorkActor actor, DateTime now, CancellationToken ct)
    {
        var lesson = await RequireLessonAsync(connection, transaction, project.Id, target, "same_as", ct);
        var key = EventKey(spec, actor, item);
        var counted = !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM lesson_events WHERE entry_id = @target AND event_hash = @hash AND counted = 1)",
            new { target, hash = Sha(key) }, transaction, cancellationToken: ct));
        bool? served = null;
        if (lesson.IsRule && actor.SessionId is { } session)
            served = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM lesson_served WHERE session_id = @session AND entry_id = @target)", new { session, target }, transaction, cancellationToken: ct));
        await InsertEventAsync(connection, transaction, project.Id, target, spec, key, counted, lesson.IsRule, served, item, actor, now, ct);

        var details = lesson.DetailsJson is { } json ? JsonSerializer.Deserialize<KnowledgeDetails>(json, WorkJson.Options) ?? new KnowledgeDetails() : new KnowledgeDetails();
        var union = UnionAppliesWhen(details.AppliesWhen, spec.Details?.AppliesWhen);
        var widened = AppliesWhenJson(union) != AppliesWhenJson(details.AppliesWhen);
        if (!counted && !widened)
            return;

        if (widened)
        {
            var merged = details with { AppliesWhen = union };
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE knowledge_entries SET details_json = @details, activities = @activities WHERE id = @target",
                new { details = DetailsJson(merged), activities = Activities(merged), target }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET recurrence = COALESCE(recurrence, 0) + @step, priority = @priority, version = version + 1, revision = revision + 1, updated_at = @now WHERE id = @target",
            new
            {
                step = counted ? 1 : 0,
                priority = counted ? BestPriority(lesson.Priority, LessonSimilarity.Priority(spec.Details?.Source?.Kind)) : lesson.Priority ?? "normal",
                now, target
            },
            transaction, cancellationToken: ct));
        await PromoteIfDueAsync(connection, transaction, target, now, spec.Details?.Source?.Kind == CorrectionSourceKind.Prompt, ct);
        await InsertRevisionAsync(connection, transaction, target, lesson.Revision + 1, counted ? "recurred" : "widened", actor, now, ct);
    }

    /// <summary>
    /// Moves a lesson's learning-loop state onto <paramref name="to"/> (record relate same_as, and a
    /// lesson superseding one): its corrections become the target's (an event the target already
    /// counts is not counted again), its contradiction links and open challenges move with it, the
    /// target's recurrence and priority are recomputed, and a rule passes its rule state on. The
    /// caller writes the revisions and the status of <paramref name="from"/>.
    /// </summary>
    private async Task TransferLessonAsync(MySqlConnection connection, MySqlTransaction transaction, KnowledgeEntryRow from, Guid to, WorkActor actor, DateTime now,
        bool uniteAppliesWhen, CancellationToken ct)
    {
        var target = (await ReadKnowledgeRowAsync(connection, transaction, to, ct))!;
        // A merge (not a replacement, whose content is its own) unites the merged lesson's applies-when into the target's.
        if (uniteAppliesWhen)
        {
            static KnowledgeDetails Details(string? json) =>
                json is null ? new KnowledgeDetails() : JsonSerializer.Deserialize<KnowledgeDetails>(json, WorkJson.Options) ?? new KnowledgeDetails();
            var targetDetails = Details(target.DetailsJson);
            var union = UnionAppliesWhen(targetDetails.AppliesWhen, Details(from.DetailsJson).AppliesWhen);
            if (AppliesWhenJson(union) != AppliesWhenJson(targetDetails.AppliesWhen))
            {
                var merged = targetDetails with { AppliesWhen = union };
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE knowledge_entries SET details_json = @details, activities = @activities WHERE id = @to",
                    new { details = DetailsJson(merged), activities = Activities(merged), to }, transaction, cancellationToken: ct));
            }
        }

        var targetEvents = (await ReadEventsAsync(connection, transaction, [to], ct))[to].Where(row => row.Counted).Select(row => row.EventHash).ToHashSet();
        foreach (var moved in (await ReadEventsAsync(connection, transaction, [from.Id], ct))[from.Id])
        {
            var counted = moved.Counted && targetEvents.Add(moved.EventHash);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE lesson_events SET entry_id = @to, counted = @counted WHERE correction_id = @correction",
                new { to, counted, correction = moved.CorrectionId }, transaction, cancellationToken: ct));
        }

        var links = (await ReadContradictionsAsync(connection, transaction, [from.Id], activeOnly: false, ct))[from.Id].Where(link => link.Id != to).ToList();
        foreach (var link in links)
            await LinkContradictionAsync(connection, transaction, to, link.Id, actor, now, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM lesson_links WHERE entry_id = @from OR other_id = @from", new { from = from.Id }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE lesson_challenges SET entry_id = @to WHERE entry_id = @from AND status = 'open'", new { to, from = from.Id }, transaction, cancellationToken: ct));

        var recurrence = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM lesson_events WHERE entry_id = @to AND counted = 1", new { to }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET recurrence = @recurrence, priority = @priority WHERE id = @to",
            new { recurrence, priority = BestPriority(target.Priority, from.Priority), to }, transaction, cancellationToken: ct));
        // A rule passes its status on: a rule merged into, or replaced by, a candidate keeps it a rule.
        if (from.IsRule && !target.IsRule)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE knowledge_entries SET rule_state = 'rule', rule_reason = @reason, rule_since = @since, rejected_reason = NULL WHERE id = @to",
                new { reason = from.RuleReason, since = from.RuleSince ?? now, to }, transaction, cancellationToken: ct));
        else
            await PromoteIfDueAsync(connection, transaction, to, now, promptEvent: false, ct);
    }

    /// <summary>
    /// Makes a lesson with an applies-when activity a rule when it is due: a correction from the
    /// human's prompt (always, R24), or its recurrence at the threshold unless it was rejected.
    /// Writes only the rule columns (the caller advances version and revision).
    /// </summary>
    private async Task<bool> PromoteIfDueAsync(MySqlConnection connection, MySqlTransaction transaction, Guid entryId, DateTime now, bool promptEvent, CancellationToken ct)
    {
        var row = await connection.QuerySingleAsync<(string? RuleState, int? Recurrence, string? Activities, string? SourceKind)>(new CommandDefinition(
            "SELECT rule_state, recurrence, activities, source_kind FROM knowledge_entries WHERE id = @entryId", new { entryId }, transaction, cancellationToken: ct));
        if (row.RuleState == "rule" || string.IsNullOrEmpty(row.Activities))
            return false;

        RuleReason? reason = promptEvent || (row.SourceKind == "prompt" && row.RuleState != "rejected") ? RuleReason.HumanPrompt
            : row.RuleState != "rejected" && (row.Recurrence ?? 1) >= ruleThreshold ? RuleReason.Recurrence
            : null;
        if (reason is null)
            return false;

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET rule_state = 'rule', rule_reason = @reason, rule_since = @now, rejected_reason = NULL WHERE id = @entryId",
            new { reason = JsonName(reason.Value), now, entryId }, transaction, cancellationToken: ct));
        return true;
    }

    // A change of a lesson made as a side effect of another write (it was linked, merged into, ...):
    // its version and revision advance, and the revision records what happened.
    private static async Task BumpLessonAsync(MySqlConnection connection, MySqlTransaction transaction, KnowledgeEntryRow before, string action, WorkActor actor, DateTime now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET version = version + 1, revision = revision + 1, updated_at = @now WHERE id = @id",
            new { now, id = before.Id }, transaction, cancellationToken: ct));
        await InsertRevisionAsync(connection, transaction, before.Id, before.Revision + 1, action, actor, now, ct);
    }

    private static Task LinkContradictionAsync(MySqlConnection connection, MySqlTransaction transaction, Guid a, Guid b, WorkActor actor, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO lesson_links (entry_id, other_id, relation, author, author_kind, author_session_id, created_at) VALUES " +
            "(@a, @b, 'contradicts', @author, @authorKind, @session, @now), (@b, @a, 'contradicts', @author, @authorKind, @session, @now)",
            new { a, b, author = actor.Name, authorKind = actor.Kind, session = actor.SessionId, now }, transaction, cancellationToken: ct));

    // ---- Lesson views ---------------------------------------------------------------------------

    private static RecurrenceView RecurrenceOf(IEnumerable<EventRow> events)
    {
        var counted = events.Where(row => row.Counted).OrderBy(row => row.CreatedAt).ThenBy(row => row.CorrectionId).ToList();
        var recurrences = counted.Skip(1).ToList();
        return new RecurrenceView(
            counted.Count,
            recurrences.Count(row => !row.RuleAtEvent),
            recurrences.Count(row => row.RuleAtEvent && row.Served == true),
            recurrences.Count(row => row.RuleAtEvent && row.Served == false),
            recurrences.Count(row => row.RuleAtEvent && row.Served is null));
    }

    private static async Task<ILookup<Guid, EventRow>> ReadEventsAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<Guid> entryIds, CancellationToken ct) =>
        entryIds.Count == 0
            ? Array.Empty<EventRow>().ToLookup(row => row.EntryId)
            : (await connection.QueryAsync<EventRow>(new CommandDefinition(
                "SELECT correction_id, entry_id, event_hash, counted, rule_at_event, served, source_kind, source_ref, summary, what, session_id, author, author_kind, created_at " +
                "FROM lesson_events WHERE entry_id IN @ids ORDER BY created_at, correction_id",
                new { ids = entryIds.Select(id => id.ToString()).ToArray() }, transaction, cancellationToken: ct))).ToLookup(row => row.EntryId);

    private static async Task<ILookup<Guid, LessonLinkView>> ReadContradictionsAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<Guid> entryIds, bool activeOnly, CancellationToken ct)
    {
        if (entryIds.Count == 0)
            return Array.Empty<(Guid, LessonLinkView)>().ToLookup(pair => pair.Item1, pair => pair.Item2);
        var rows = await connection.QueryAsync<(Guid EntryId, Guid OtherId, string Summary, string Status, string? RuleState)>(new CommandDefinition(
            "SELECT l.entry_id, l.other_id, o.summary, o.status, o.rule_state FROM lesson_links l JOIN knowledge_entries o ON o.id = l.other_id " +
            $"WHERE l.entry_id IN @ids AND l.relation = 'contradicts'{(activeOnly ? " AND o.status = 'active'" : "")} ORDER BY o.created_at, o.id",
            new { ids = entryIds.Select(id => id.ToString()).ToArray() }, transaction, cancellationToken: ct));
        return rows.ToLookup(row => row.EntryId, row => new LessonLinkView(row.OtherId, row.Summary, ParseName<KnowledgeStatus>(row.Status),
            row.RuleState is { } state ? ParseName<LessonRuleState>(state) : null));
    }

    private static async Task<ILookup<Guid, ChallengeView>> ReadChallengesAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<Guid> entryIds, bool openOnly, CancellationToken ct)
    {
        if (entryIds.Count == 0)
            return Array.Empty<ChallengeView>().ToLookup(view => view.EntryId);
        var rows = await connection.QueryAsync<ChallengeRow>(new CommandDefinition(
            $"SELECT {ChallengeColumns} FROM lesson_challenges c WHERE c.entry_id IN @ids{(openOnly ? " AND c.status = 'open'" : "")} ORDER BY c.created_at, c.id",
            new { ids = entryIds.Select(id => id.ToString()).ToArray() }, transaction, cancellationToken: ct));
        return rows.Select(ToView).ToLookup(view => view.EntryId);
    }

    private static async Task<ILookup<Guid, AddressedByView>> ReadAddressedByAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<Guid> entryIds, CancellationToken ct)
    {
        if (entryIds.Count == 0)
            return Array.Empty<(Guid, AddressedByView)>().ToLookup(pair => pair.Item1, pair => pair.Item2);
        var rows = await connection.QueryAsync<(Guid EntryId, string Kind, string Name, string Harness, string? Ref, string Author, string AuthorKind, string? AuthorSessionId, DateTime CreatedAt)>(new CommandDefinition(
            "SELECT entry_id, kind, name, harness, ref, author, author_kind, author_session_id, created_at FROM lesson_addressed_by WHERE entry_id IN @ids ORDER BY created_at, kind, name",
            new { ids = entryIds.Select(id => id.ToString()).ToArray() }, transaction, cancellationToken: ct));
        return rows.ToLookup(row => row.EntryId, row => new AddressedByView(ParseName<AddressedByKind>(row.Kind), row.Name, row.Ref,
            new WorkActor(row.Author, row.AuthorKind, row.AuthorSessionId), Utc(row.CreatedAt), row.Harness.Length == 0 ? null : row.Harness));
    }

    private static async Task<Dictionary<Guid, (int Sessions, DateTime? Last)>> ReadServedAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<Guid> entryIds, CancellationToken ct) =>
        entryIds.Count == 0
            ? []
            : (await connection.QueryAsync<(Guid EntryId, long Sessions, DateTime? Last)>(new CommandDefinition(
                "SELECT entry_id, COUNT(*) AS sessions, MAX(last_served_at) AS last FROM lesson_served WHERE entry_id IN @ids GROUP BY entry_id",
                new { ids = entryIds.Select(id => id.ToString()).ToArray() }, transaction, cancellationToken: ct)))
            .ToDictionary(row => row.EntryId, row => ((int)row.Sessions, row.Last is { } last ? Utc(last) : (DateTime?)null));

    private static async Task<LessonView> LoadLessonViewAsync(MySqlConnection connection, MySqlTransaction? transaction, KnowledgeEntryRow row, bool withEvents, CancellationToken ct)
    {
        IReadOnlyList<Guid> ids = [row.Id];
        var events = (await ReadEventsAsync(connection, transaction, ids, ct))[row.Id].ToList();
        var contradicts = (await ReadContradictionsAsync(connection, transaction, ids, activeOnly: false, ct))[row.Id].ToList();
        var challenges = (await ReadChallengesAsync(connection, transaction, ids, openOnly: false, ct))[row.Id]
            .OrderBy(challenge => challenge.Status == ChallengeStatus.Open ? 0 : 1).ThenByDescending(challenge => challenge.CreatedAt).ToList();
        var addressed = (await ReadAddressedByAsync(connection, transaction, ids, ct))[row.Id].ToList();
        var served = await ReadServedAsync(connection, transaction, ids, ct);
        var (sessions, last) = served.TryGetValue(row.Id, out var found) ? found : (0, null);
        return new LessonView(
            row.RuleState is { } state ? ParseName<LessonRuleState>(state) : LessonRuleState.Candidate,
            row.Priority ?? "normal",
            RecurrenceOf(events),
            row.RuleReason is { } reason ? ParseName<RuleReason>(reason) : null,
            row.RuleSince is { } since ? Utc(since) : null,
            row.RejectedReason,
            contradicts.Count == 0 ? null : contradicts,
            challenges.Count == 0 ? null : challenges,
            addressed.Count == 0 ? null : addressed,
            sessions == 0 ? null : sessions,
            last,
            withEvents
                ? events.Select(item => new LessonEventView(item.CorrectionId, item.Counted, item.Summary, new WorkActor(item.Author, item.AuthorKind, item.SessionId),
                    Utc(item.CreatedAt), item.SourceKind is { } kind ? ParseName<CorrectionSourceKind>(kind) : null, item.SourceRef, item.What,
                    item.RuleAtEvent ? true : null, item.Served, item.SessionId)).ToList()
                : null);
    }

    // ---- Similarity -------------------------------------------------------------------------------
    //
    // Embeddings find candidates; they never decide (§5.7, E5). With an embedding provider each
    // lesson's summary is embedded once per model and stored (lesson_embeddings, re-embedded when the
    // summary changes); candidates are the lessons at or above the provider's cosine threshold. A
    // lesson with no vector yet beyond this call's embedding allowance, or every lesson when there is
    // no provider (or it fails), is compared by the lexical score. The polarity flag is lexical.

    private sealed record PoolLesson(Guid Id, string Summary, string SummaryHash, long Version, string? RuleState);

    public async Task<IReadOnlyList<SimilarLessons>> FindSimilarLessonsAsync(Guid projectId, IReadOnlyList<Guid> entryIds, LessonEmbedder? embedder, CancellationToken ct = default)
    {
        if (entryIds.Count == 0)
            return [];
        await using var connection = await OpenAsync(ct);
        var pool = (await connection.QueryAsync<PoolLesson>(new CommandDefinition(
            "SELECT id, summary, summary_hash, version, rule_state FROM knowledge_entries WHERE project_id = @projectId AND kind = 'lesson' AND status = 'active' " +
            $"ORDER BY created_at DESC, id LIMIT {LessonLimits.MaxSimilarityScan}",
            new { projectId }, cancellationToken: ct))).ToList();
        var subjects = (await connection.QueryAsync<PoolLesson>(new CommandDefinition(
            "SELECT id, summary, summary_hash, version, rule_state FROM knowledge_entries WHERE id IN @ids AND kind = 'lesson'",
            new { ids = entryIds.Distinct().Select(id => id.ToString()).ToArray() }, cancellationToken: ct))).ToDictionary(row => row.Id);
        var linked = await ReadContradictionsAsync(connection, null, subjects.Keys.ToList(), activeOnly: false, ct);
        var vectors = embedder is null
            ? null
            : await LessonVectorsAsync(connection, subjects.Values.Concat(pool).DistinctBy(lesson => lesson.Id).ToList(), embedder, ct);

        var answered = new List<SimilarLessons>();
        foreach (var id in entryIds.Distinct())
        {
            if (!subjects.TryGetValue(id, out var subject))
                continue;
            var decided = linked[id].Select(link => link.Id).ToHashSet();
            var negates = LessonSimilarity.Negates(subject.Summary);
            float[]? own = null;
            vectors?.TryGetValue(id, out own);
            var candidates = pool
                .Where(candidate => candidate.Id != id && !decided.Contains(candidate.Id))
                .Select(candidate =>
                {
                    float[]? other = null;
                    if (own is not null && vectors!.TryGetValue(candidate.Id, out other))
                    {
                        var cosine = Math.Round(Cosine(own, other), 3);
                        return (candidate, score: cosine, method: "embedding", passes: cosine >= embedder!.Threshold);
                    }

                    var lexical = LessonSimilarity.Score(subject.Summary, candidate.Summary);
                    return (candidate, score: lexical, method: "lexical", passes: lexical >= LessonSimilarity.CandidateThreshold);
                })
                .Where(scored => scored.passes)
                .OrderByDescending(scored => scored.score).ThenBy(scored => scored.candidate.Id)
                .Take(LessonLimits.MaxSimilar)
                .Select(scored => new SimilarLesson(scored.candidate.Id, scored.candidate.Summary, scored.score, scored.candidate.Version,
                    scored.candidate.RuleState is { } state ? ParseName<LessonRuleState>(state) : LessonRuleState.Candidate,
                    LessonSimilarity.Negates(scored.candidate.Summary) != negates ? true : null,
                    scored.method))
                .ToList();
            if (candidates.Count > 0)
                answered.Add(new SimilarLessons(id, candidates));
        }

        return answered;
    }

    // The stored vectors of the lessons for the embedder's model, embedding (and storing) the ones
    // missing or stale, subjects first, at most MaxEmbedPerCall. Null when the provider fails: the
    // lookup then falls back to the lexical score; a write never fails for similarity.
    private async Task<Dictionary<Guid, float[]>?> LessonVectorsAsync(MySqlConnection connection, IReadOnlyList<PoolLesson> lessons, LessonEmbedder embedder, CancellationToken ct)
    {
        var stored = (await connection.QueryAsync<(Guid EntryId, string SummaryHash, byte[] Vector)>(new CommandDefinition(
            "SELECT entry_id, summary_hash, vector FROM lesson_embeddings WHERE model = @model AND entry_id IN @ids",
            new { model = embedder.Model, ids = lessons.Select(lesson => lesson.Id.ToString()).ToArray() }, cancellationToken: ct))).ToList();
        var hashes = lessons.ToDictionary(lesson => lesson.Id, lesson => lesson.SummaryHash);
        var vectors = stored.Where(row => hashes.TryGetValue(row.EntryId, out var hash) && hash == row.SummaryHash)
            .ToDictionary(row => row.EntryId, row => ToFloats(row.Vector));
        var missing = lessons.Where(lesson => !vectors.ContainsKey(lesson.Id)).Take(LessonLimits.MaxEmbedPerCall).ToList();
        if (missing.Count == 0)
            return vectors;

        IReadOnlyList<float[]> embedded;
        try
        {
            embedded = embedder.Embed(missing.Select(lesson => lesson.Summary).ToList());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(log, exception, "Lesson embedding with {Model} failed; similarity falls back to the lexical score.", embedder.Model);
            return null;
        }

        if (embedded.Count != missing.Count)
            return null;
        var now = Now();
        await connection.ExecuteAsync(new CommandDefinition(
            "REPLACE INTO lesson_embeddings (entry_id, model, summary_hash, dimensions, vector, created_at) VALUES (@entryId, @model, @hash, @dimensions, @vector, @now)",
            missing.Select((lesson, index) => new
            {
                entryId = lesson.Id, model = embedder.Model, hash = lesson.SummaryHash, dimensions = embedded[index].Length, vector = ToBytes(embedded[index]), now
            }), cancellationToken: ct));
        for (var index = 0; index < missing.Count; index++)
            vectors[missing[index].Id] = embedded[index];
        return vectors;
    }

    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ToFloats(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;
        double dot = 0, left = 0, right = 0;
        for (var index = 0; index < a.Length; index++)
        {
            dot += a[index] * b[index];
            left += a[index] * a[index];
            right += b[index] * b[index];
        }

        return left == 0 || right == 0 ? 0 : dot / (Math.Sqrt(left) * Math.Sqrt(right));
    }

    // ---- Rules in briefings ---------------------------------------------------------------------

    private const string ActiveRule = "e.project_id = @projectId AND e.kind = 'lesson' AND e.status = 'active' AND e.rule_state = 'rule'";

    // Addressed locally, by scope (§5.7): a test or CI gate takes the rule out of every briefing.
    private const string AddressedEverywhere = "EXISTS (SELECT 1 FROM lesson_addressed_by ab WHERE ab.entry_id = e.id AND ab.kind IN ('test', 'ci'))";

    public async Task<IReadOnlyList<BriefingRule>> ReadRulesAsync(RuleContext context, CancellationToken ct = default)
    {
        if (context.Activities.Count == 0)
            return [];
        await using var connection = await OpenAsync(ct);
        var parameters = new DynamicParameters();
        parameters.Add("projectId", context.ProjectId);
        var clauses = new List<string>();
        foreach (var (activity, index) in context.Activities.Distinct().Select((activity, index) => (activity, index)))
        {
            clauses.Add($"FIND_IN_SET(@a{index}, e.activities) > 0");
            parameters.Add($"a{index}", JsonName(activity));
        }

        // Addressed for this session: a skill it declared loaded, an instruction or hook of its harness.
        var addressed = new List<string> { "ab.kind IN ('test', 'ci')" };
        var skills = (context.Skills ?? []).Select(skill => skill.Trim().ToLowerInvariant()).Where(skill => skill.Length > 0).Distinct().ToArray();
        if (skills.Length > 0)
        {
            addressed.Add("(ab.kind = 'skill' AND LOWER(ab.name) IN @skills)");
            parameters.Add("skills", skills);
        }

        // The location filters in SQL, before the cap, so a matching rule is never crowded out.
        var location = new StringBuilder();
        const string repositories = "JSON_EXTRACT(e.details_json, '$.applies_when.repositories')";
        const string harness = "JSON_VALUE(e.details_json, '$.applies_when.harness')";
        if (context.Repository is { Length: > 0 } repository)
        {
            location.Append($" AND ({repositories} IS NULL OR JSON_CONTAINS(LOWER({repositories}), LOWER(JSON_QUOTE(@repository))))");
            parameters.Add("repository", repository);
        }
        else
        {
            location.Append($" AND {repositories} IS NULL");
        }

        if (context.Harness is { Length: > 0 } sessionHarness)
        {
            addressed.Add("(ab.kind IN ('instruction', 'hook') AND LOWER(ab.harness) = LOWER(@harness))");
            location.Append($" AND ({harness} IS NULL OR LOWER({harness}) = LOWER(@harness))");
            parameters.Add("harness", sessionHarness);
        }
        else
        {
            location.Append($" AND {harness} IS NULL");
        }

        if (context.Paths.Count == 0)
            location.Append(" AND JSON_EXTRACT(e.details_json, '$.applies_when.paths') IS NULL");

        var rows = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE {ActiveRule} AND ({string.Join(" OR ", clauses)}){location} " +
            $"AND NOT EXISTS (SELECT 1 FROM lesson_addressed_by ab WHERE ab.entry_id = e.id AND ({string.Join(" OR ", addressed)})) " +
            "ORDER BY CASE e.priority WHEN 'highest' THEN 0 WHEN 'high' THEN 1 ELSE 2 END, COALESCE(e.recurrence, 1) DESC, e.updated_at DESC, e.id " +
            $"LIMIT {MaxRelevantCandidates}",
            parameters, cancellationToken: ct))).ToList();

        var chosen = rows
            .Select(row => (row, details: row.DetailsJson is { } json ? JsonSerializer.Deserialize<KnowledgeDetails>(json, WorkJson.Options) : null))
            .Where(pair => LocationMatches(pair.details?.AppliesWhen, context))
            .Take(context.Limit)
            .ToList();
        var ids = chosen.Select(pair => pair.row.Id).ToList();
        var contradicts = await ReadContradictionsAsync(connection, null, ids, activeOnly: true, ct);
        var challenges = await ReadChallengesAsync(connection, null, ids, openOnly: true, ct);
        return chosen.Select(pair =>
        {
            var links = contradicts[pair.row.Id].ToList();
            var open = challenges[pair.row.Id].ToList();
            var shown = open.Take(LessonLimits.MaxShownChallenges).Select(challenge => new BriefingChallenge(challenge.Id, challenge.Reason,
                challenge.Evidence.Select(evidence => evidence.Ref is null ? evidence.Summary : $"{evidence.Summary} ({evidence.Ref})").ToList())).ToList();
            return new BriefingRule(pair.row.Id, pair.row.Summary, pair.row.Version, pair.row.Priority ?? "normal", pair.details?.AppliesWhen?.Activity ?? [],
                pair.row.Recurrence is > 1 ? pair.row.Recurrence : null,
                links.Count == 0 ? null : links,
                shown.Count == 0 ? null : shown,
                open.Count > shown.Count ? open.Count - shown.Count : null);
        }).ToList();
    }

    // A rule's location (optional) must match the session: its repositories name the session's, one
    // of its paths covers a path the session works on, its harness is the session's. Tools are not
    // known for a session, so a rule naming tools is served by activity and location alone. (The
    // query filters repositories and harness already; paths, being globs, are matched here.)
    private static bool LocationMatches(AppliesWhenView? appliesWhen, RuleContext context)
    {
        if (appliesWhen is null)
            return true;
        if (appliesWhen.Repositories is { Count: > 0 } repositories &&
            (context.Repository is null || !repositories.Contains(context.Repository, StringComparer.OrdinalIgnoreCase)))
            return false;
        if (appliesWhen.Paths is { Count: > 0 } paths && !paths.Any(path => context.Paths.Any(query => KnowledgePaths.Covers(path, query))))
            return false;
        return appliesWhen.Harness is null || string.Equals(appliesWhen.Harness, context.Harness, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<LessonActivity>> ReadRuleActivitiesAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var lists = await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT DISTINCT e.activities FROM knowledge_entries e WHERE {ActiveRule} AND NOT {AddressedEverywhere} AND e.activities IS NOT NULL",
            new { projectId }, cancellationToken: ct));
        return lists.SelectMany(list => list.Split(',', StringSplitOptions.RemoveEmptyEntries)).Distinct()
            .Select(ParseName<LessonActivity>).Order().ToList();
    }

    public async Task LogServedRulesAsync(Guid sessionId, Guid projectId, IReadOnlyList<Guid> ruleIds, CancellationToken ct = default)
    {
        if (ruleIds.Count == 0)
            return;
        var now = Now();
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO lesson_served (session_id, entry_id, project_id, first_served_at, last_served_at, times) VALUES (@session, @entryId, @projectId, @now, @now, 1) " +
            "ON DUPLICATE KEY UPDATE last_served_at = @now, times = times + 1",
            ruleIds.Distinct().Select(entryId => new { session = sessionId.ToString(), entryId, projectId, now }), cancellationToken: ct));
    }

    public async Task SetSessionBriefingContextAsync(Guid sessionId, IReadOnlyList<LessonActivity>? activity, IReadOnlyList<string>? skills, CancellationToken ct = default)
    {
        if (activity is null && skills is null)
            return;
        await using var connection = await OpenAsync(ct);
        var sets = new List<string>();
        if (activity is not null)
            sets.Add("activities = @activities");
        if (skills is not null)
            sets.Add("skills = @skills");
        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE work_sessions SET {string.Join(", ", sets)} WHERE id = @id",
            new { activities = ActivityList(activity), skills = SkillList(skills), id = sessionId.ToString() }, cancellationToken: ct));
    }

    private static string? ActivityList(IReadOnlyList<LessonActivity>? activity) =>
        activity is { Count: > 0 } ? string.Join(',', activity.Distinct().Select(JsonName)) : null;

    private static IReadOnlyList<LessonActivity>? ParseActivities(string? stored) =>
        string.IsNullOrEmpty(stored) ? null : stored.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ParseName<LessonActivity>).ToList();

    private static string? SkillList(IReadOnlyList<string>? skills) =>
        skills is { Count: > 0 } ? JsonSerializer.Serialize(skills.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), WorkJson.Options) : null;

    private static IReadOnlyList<string>? ParseSkills(string? stored) =>
        string.IsNullOrEmpty(stored) ? null : JsonSerializer.Deserialize<List<string>>(stored, WorkJson.Options);

    // ---- record relate / acknowledge ----------------------------------------------------------------

    public Task<KnowledgeView> RelateLessonAsync(Guid entryId, long expectedVersion, Guid? sameAs, Guid? contradicts, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var other = sameAs ?? contradicts ?? throw WorkException.Invalid("relate needs same_as or contradicts. Nothing was changed.");
            if (other == entryId)
                throw WorkException.Invalid("A lesson cannot be related to itself. Nothing was changed.");
            var projectId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT project_id FROM knowledge_entries WHERE id = @entryId", new { entryId }, transaction, cancellationToken: ct))
                ?? throw WorkException.NotFound($"There is no knowledge entry {entryId}.");
            var project = await HoldProjectAsync(connection, transaction, projectId, ct);
            // Both rows, in id order.
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT id FROM knowledge_entries WHERE id IN @ids ORDER BY id FOR UPDATE",
                new { ids = new[] { entryId.ToString(), other.ToString() } }, transaction, cancellationToken: ct));

            var before = await AdvanceKnowledgeAsync(connection, transaction, entryId, expectedVersion, now, ct);
            if (before.Kind != "lesson")
                throw WorkException.Invalid($"Entry {entryId} is a {before.Kind}; relate takes lessons. Nothing was changed.");
            var target = await RequireLessonAsync(connection, transaction, project.Id, other, sameAs is not null ? "relate.same_as" : "relate.contradicts", ct);

            if (contradicts is not null)
            {
                await LinkContradictionAsync(connection, transaction, entryId, other, actor, now, ct);
                await InsertRevisionAsync(connection, transaction, entryId, before.Revision, "contradicted", actor, now, ct);
                await BumpLessonAsync(connection, transaction, target, "contradicted", actor, now, ct);
                var linked = (await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct))!;
                await transaction.CommitAsync(ct);
                return linked;
            }

            // same_as: the lesson is merged into the target and superseded by it; its corrections,
            // links, open challenges, applies-when and rule status pass to the target.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE knowledge_entries SET status = 'superseded', superseded_by_id = @target, status_reason = @reason WHERE id = @entryId",
                new { target = other, reason = $"Merged into {other}: the same lesson.", entryId }, transaction, cancellationToken: ct));
            await TransferLessonAsync(connection, transaction, before, other, actor, now, uniteAppliesWhen: true, ct);

            await InsertRevisionAsync(connection, transaction, entryId, before.Revision, "merged", actor, now, ct);
            await BumpLessonAsync(connection, transaction, target, "merged_into", actor, now, ct);
            var view = (await LoadKnowledgeViewAsync(connection, transaction, other, withRevisions: false, ct))!;
            await transaction.CommitAsync(ct);
            return view;
        }, ct);

    public Task<KnowledgeView> AcknowledgeKnowledgeAsync(Guid entryId, long expectedVersion, WorkActor actor, CancellationToken ct = default) =>
        ChangeKnowledgeAsync(entryId, expectedVersion, "acknowledged", actor, async (connection, transaction, before, now) =>
        {
            if (before.Kind != "decision" || before.MadeAutonomously != true)
                throw WorkException.Invalid($"Entry {entryId} is not a decision made autonomously; only those are in the review queue to acknowledge. Nothing was changed.");
            var reviewed = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT reviewed_at FROM knowledge_entries WHERE id = @entryId", new { entryId }, transaction, cancellationToken: ct));
            if (reviewed is not null)
                throw WorkException.Invalid($"Decision {entryId} was already acknowledged. Nothing was changed.");
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE knowledge_entries SET reviewed_at = @now, reviewed_by = @by WHERE id = @entryId",
                new { now, by = actor.Name, entryId }, transaction, cancellationToken: ct));
        }, ct);

    // ---- promote -----------------------------------------------------------------------------------

    public Task<PromoteResult> PromoteLessonAsync(Guid entryId, long expectedVersion, PromoteAction action, string? reason, AddressedBySpec? addressedBy, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var projectId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT project_id FROM knowledge_entries WHERE id = @entryId", new { entryId }, transaction, cancellationToken: ct))
                ?? throw WorkException.NotFound($"There is no knowledge entry {entryId}.");
            await HoldProjectAsync(connection, transaction, projectId, ct);
            var row = await RequireLessonAsync(connection, transaction, projectId, entryId, "id", ct);
            var state = row.RuleState is { } stored ? ParseName<LessonRuleState>(stored) : LessonRuleState.Candidate;

            // A call that would change nothing answers the lesson (a retry is safe), whatever version it read.
            var unchanged = action switch
            {
                PromoteAction.Propose => state == LessonRuleState.Proposed,
                PromoteAction.Approve => state == LessonRuleState.Rule,
                PromoteAction.Reject => state == LessonRuleState.Rejected,
                // The same fix (kind, name, harness) with the same ref; a new ref updates the link.
                _ => await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM lesson_addressed_by WHERE entry_id = @entryId AND kind = @kind AND name = @name AND harness = @harness AND ref <=> @ref)",
                    new { entryId, kind = JsonName(addressedBy!.Kind), name = addressedBy.Name, harness = addressedBy.Harness ?? "", @ref = addressedBy.Ref },
                    transaction, cancellationToken: ct))
            };
            if (unchanged)
            {
                var current = (await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct))!;
                await transaction.CommitAsync(ct);
                return new PromoteResult(current, false);
            }

            if (row.Version != expectedVersion)
                throw new WorkException(WorkErrorCodes.Conflict,
                    $"Lesson {entryId} is at version {row.Version}, not {expectedVersion}: it was changed after it was read. Nothing was changed; read it again (in current) and repeat the call.",
                    await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct));

            var hasActivity = !string.IsNullOrEmpty((await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT activities FROM knowledge_entries WHERE id = @entryId", new { entryId }, transaction, cancellationToken: ct))));
            if (action is PromoteAction.Propose or PromoteAction.Approve && !hasActivity)
                throw WorkException.Invalid(
                    $"Lesson {entryId} has no applies-when activity: a rule is served when its activity matches, and one without would be served everywhere. " +
                    "Revise it with applies_when.activity (record revise) first. Nothing was changed.");

            switch (action)
            {
                case PromoteAction.Propose when state == LessonRuleState.Rule:
                    throw new WorkException(WorkErrorCodes.Conflict, $"Lesson {entryId} is already a rule. Nothing was changed.",
                        await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct));
                case PromoteAction.Propose when state == LessonRuleState.Rejected:
                    throw new WorkException(WorkErrorCodes.Conflict,
                        $"Lesson {entryId} was rejected for rule status ({row.RejectedReason}), so it is not proposed again. promote approve overrides the rejection. Nothing was changed.",
                        await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct));
                case PromoteAction.Propose:
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE knowledge_entries SET rule_state = 'proposed' WHERE id = @entryId", new { entryId }, transaction, cancellationToken: ct));
                    break;
                case PromoteAction.Approve:
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE knowledge_entries SET rule_state = 'rule', rule_reason = 'approved', rule_since = @now, rejected_reason = NULL WHERE id = @entryId",
                        new { now, entryId }, transaction, cancellationToken: ct));
                    break;
                case PromoteAction.Reject when state == LessonRuleState.Rule:
                    throw WorkException.Invalid($"Lesson {entryId} is a rule: challenge it with evidence, or retract it with record retract. reject refuses rule status to a candidate. Nothing was changed.");
                case PromoteAction.Reject:
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE knowledge_entries SET rule_state = 'rejected', rejected_reason = @reason WHERE id = @entryId", new { reason, entryId }, transaction, cancellationToken: ct));
                    break;
                default:
                    await connection.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO lesson_addressed_by (entry_id, kind, name, harness, ref, author, author_kind, author_session_id, created_at) " +
                        "VALUES (@entryId, @kind, @name, @harness, @ref, @author, @authorKind, @session, @now) " +
                        "ON DUPLICATE KEY UPDATE ref = @ref, author = @author, author_kind = @authorKind, author_session_id = @session, created_at = @now",
                        new
                        {
                            entryId, kind = JsonName(addressedBy!.Kind), name = addressedBy.Name, harness = addressedBy.Harness ?? "", @ref = addressedBy.Ref,
                            author = actor.Name, authorKind = actor.Kind, session = actor.SessionId, now
                        },
                        transaction, cancellationToken: ct));
                    break;
            }

            await BumpLessonAsync(connection, transaction, row, action switch
            {
                PromoteAction.Propose => "proposed",
                PromoteAction.Approve => "approved",
                PromoteAction.Reject => "rejected",
                _ => "addressed_by"
            }, actor, now, ct);
            var view = (await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct))!;
            await transaction.CommitAsync(ct);
            return new PromoteResult(view, true);
        }, ct);

    // ---- challenge ---------------------------------------------------------------------------------

    public Task<ChallengeResult> OpenChallengeAsync(Guid entryId, string reason, IReadOnlyList<EvidenceView> evidence, string? keyHash, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var projectId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT project_id FROM knowledge_entries WHERE id = @entryId", new { entryId }, transaction, cancellationToken: ct))
                ?? throw WorkException.NotFound($"There is no knowledge entry {entryId}.");
            await HoldProjectAsync(connection, transaction, projectId, ct);
            if (keyHash is not null && await FindKeyAsync(connection, transaction, projectId, ChallengeOperation, keyHash, ct) is { EntityId: { } existing })
            {
                var earlier = (await ReadChallengeRowAsync(connection, transaction, existing, ct))!;
                var rule = (await LoadKnowledgeViewAsync(connection, transaction, earlier.EntryId, withRevisions: false, ct))!;
                await transaction.CommitAsync(ct);
                return new ChallengeResult(false, ToView(earlier), rule);
            }

            var row = await RequireLessonAsync(connection, transaction, projectId, entryId, "open.id", ct);
            if (!row.IsRule)
                throw WorkException.Invalid($"Lesson {entryId} is not a rule ({row.RuleState}): challenge disputes an active rule. A lesson that is not a rule is not served; retract or revise it instead. Nothing was changed.");

            var id = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO lesson_challenges (id, entry_id, project_id, status, reason, evidence_json, author, author_kind, author_session_id, author_harness, created_at) " +
                "VALUES (@id, @entryId, @projectId, 'open', @reason, @evidence, @author, @authorKind, @session, @harness, @now)",
                new
                {
                    id, entryId, projectId, reason, evidence = JsonSerializer.Serialize(evidence, WorkJson.Options),
                    author = actor.Name, authorKind = actor.Kind, session = actor.SessionId, harness = actor.Harness, now
                },
                transaction, cancellationToken: ct));
            if (keyHash is not null)
                await InsertKeyAsync(connection, transaction, projectId, ChallengeOperation, keyHash, id, null, now, ct);
            // The rule stays active and unchanged: the challenge is shown with it.
            var challenge = (await ReadChallengeRowAsync(connection, transaction, id, ct))!;
            var view = (await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct))!;
            await transaction.CommitAsync(ct);
            return new ChallengeResult(true, ToView(challenge), view);
        }, ct);

    public Task<ChallengeResult> ResolveChallengeAsync(Guid challengeId, long expectedVersion, ChallengeOutcome outcome, string reason, KnowledgeSpec? amended, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var found = await ReadChallengeRowAsync(connection, transaction, challengeId, ct)
                ?? throw WorkException.NotFound($"There is no challenge {challengeId}.");
            // Lock order: project, then the rule's knowledge row, then the challenge row. Holding the
            // project serialises lesson writes, so the challenge's rule cannot move meanwhile.
            await HoldProjectAsync(connection, transaction, found.ProjectId, ct);
            var ruleId = (await ReadChallengeRowAsync(connection, transaction, challengeId, ct))!.EntryId;
            var before = await AdvanceKnowledgeAsync(connection, transaction, ruleId, expectedVersion, now, ct);
            var challenge = (await connection.QuerySingleAsync<ChallengeRow>(new CommandDefinition(
                $"SELECT {ChallengeColumns} FROM lesson_challenges c WHERE c.id = @challengeId FOR UPDATE", new { challengeId }, transaction, cancellationToken: ct)));
            if (challenge.Status != "open")
                throw new WorkException(WorkErrorCodes.Conflict, $"Challenge {challengeId} is already {challenge.Status}. Nothing was changed.", ToView(challenge));
            string action;
            switch (outcome)
            {
                case ChallengeOutcome.Amend:
                    await ApplyRevisionAsync(connection, transaction, challenge.EntryId, before, amended!, now, ct);
                    action = "amended";
                    break;
                case ChallengeOutcome.Retract:
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE knowledge_entries SET status = 'retracted', status_reason = @reason WHERE id = @id",
                        new { reason, id = challenge.EntryId }, transaction, cancellationToken: ct));
                    action = "retracted";
                    break;
                default:
                    action = "upheld";
                    break;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE lesson_challenges SET status = @status, resolution_reason = @reason, resolved_by = @by, resolved_by_kind = @byKind, resolved_by_session_id = @bySession, " +
                "resolved_at = @now WHERE id = @challengeId",
                new
                {
                    status = JsonName(outcome switch { ChallengeOutcome.Amend => ChallengeStatus.Amended, ChallengeOutcome.Retract => ChallengeStatus.Retracted, _ => ChallengeStatus.Upheld }),
                    reason, by = actor.Name, byKind = actor.Kind, bySession = actor.SessionId, now, challengeId
                },
                transaction, cancellationToken: ct));
            await InsertRevisionAsync(connection, transaction, challenge.EntryId, before.Revision, action, actor, now, ct);
            var resolved = (await ReadChallengeRowAsync(connection, transaction, challengeId, ct))!;
            var rule = (await LoadKnowledgeViewAsync(connection, transaction, challenge.EntryId, withRevisions: false, ct))!;
            await transaction.CommitAsync(ct);
            return new ChallengeResult(false, ToView(resolved), rule);
        }, ct);

    private static Task<ChallengeRow?> ReadChallengeRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid challengeId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ChallengeRow>(new CommandDefinition(
            $"SELECT {ChallengeColumns} FROM lesson_challenges c WHERE c.id = @challengeId", new { challengeId }, transaction, cancellationToken: ct));

    public async Task<ChallengeView?> GetChallengeAsync(Guid challengeId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadChallengeRowAsync(connection, null, challengeId, ct) is { } row ? ToView(row) : null;
    }

    // ---- lessons (the Lessons page's data) --------------------------------------------------------
    //
    // Seam for GitHub evidence (S6): every correction keeps its source event in lesson_events
    // (source_kind review / bot_review, source_ref the review comment's id or URL). "Review comments
    // with no lesson" is the review evidence of an item whose comment ids have no lesson_events row
    // with that source_ref; it belongs beside these views once IWorkStore exposes that evidence.

    public async Task<LessonsPage> ReadLessonsAsync(LessonsSpec spec, CancellationToken ct = default)
    {
        if (spec.View == LessonsView.UnlearnedReviews)
            return await ReadUnlearnedReviewsAsync(spec, ct);
        await using var connection = await OpenAsync(ct);
        var parameters = new DynamicParameters();
        parameters.Add("projectId", spec.ProjectId);
        var where = new StringBuilder("e.project_id = @projectId AND e.kind = 'lesson' AND e.status = 'active'");
        if (spec.Activity is { } activity)
        {
            where.Append(" AND FIND_IN_SET(@activity, e.activities) > 0");
            parameters.Add("activity", JsonName(activity));
        }

        if (spec.Source is { } source)
        {
            where.Append(" AND e.source_kind = @source");
            parameters.Add("source", JsonName(source));
        }

        string order;
        switch (spec.View)
        {
            case LessonsView.RecentlyLearned:
                where.Append(" AND e.rule_state = 'rule'");
                order = "e.rule_since DESC, e.id";
                break;
            case LessonsView.Recurrence:
                where.Append(" AND e.recurrence > 1");
                order = "e.recurrence DESC, e.updated_at DESC, e.id";
                break;
            case LessonsView.Unaddressed:
                where.Append(" AND e.rule_state = 'rule' AND " + NotAddressed);
                order = "e.activities, e.recurrence DESC, e.id";
                break;
            default:
                where.Append(" AND e.rule_state = 'rule' AND " + NotAddressed +
                             " AND NOT EXISTS (SELECT 1 FROM lesson_served s WHERE s.entry_id = e.id AND s.last_served_at >= @since)");
                parameters.Add("since", Now().AddDays(-spec.SinceDays));
                order = "(SELECT MAX(s.last_served_at) FROM lesson_served s WHERE s.entry_id = e.id), e.rule_since, e.id";
                break;
        }

        var rows = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE {where} ORDER BY {order} LIMIT {spec.Limit + 1} OFFSET {spec.Offset}",
            parameters, cancellationToken: ct))).ToList();
        var more = rows.Count > spec.Limit;
        rows = rows.Take(spec.Limit).ToList();
        return new LessonsPage(await LessonRowsAsync(connection, rows, ct), more);
    }

    // "Review comments with no lesson" (§5.7): the GitHub review evidence (S6) of the project's items
    // (reviews with a body and review comments, of the current attempt) whose URL or id no correction
    // names as its source.ref. Newest first. source review/bot_review narrows to human or bot reviews.
    private sealed class UnlearnedRow
    {
        public Guid Id { get; init; }
        public string ProjectKey { get; init; } = "";
        public int Number { get; init; }
        public string Kind { get; init; } = "";
        public string AuthorLogin { get; init; } = "";
        public string Source { get; init; } = "";
        public string Priority { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public string? Url { get; init; }
        public string? ExternalId { get; init; }
        public string? ReviewState { get; init; }
        public string? Body { get; init; }
    }

    private async Task<LessonsPage> ReadUnlearnedReviewsAsync(LessonsSpec spec, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var source = spec.Source switch
        {
            CorrectionSourceKind.Review => GitHubEvidenceSources.Human,
            CorrectionSourceKind.BotReview => GitHubEvidenceSources.Bot,
            _ => null
        };
        var rows = (await connection.QueryAsync<UnlearnedRow>(new CommandDefinition(
            "SELECT g.id, p.project_key, i.number, g.kind, g.author_login, g.source, g.priority, g.created_at, g.url, g.external_id, g.review_state, " +
            "LEFT(g.body, 400) AS body FROM work_github_evidence g JOIN work_items i ON i.id = g.item_id JOIN work_projects p ON p.id = i.project_id " +
            "WHERE i.project_id = @projectId AND g.superseded = 0 AND (g.kind = 'review_comment' OR (g.kind = 'review' AND g.body IS NOT NULL AND g.body <> '')) " +
            (source is null ? "" : "AND g.source = @source ") +
            "AND NOT EXISTS (SELECT 1 FROM lesson_events le WHERE le.project_id = i.project_id AND le.source_ref IS NOT NULL AND " +
            "(LOWER(le.source_ref) = LOWER(g.url) OR le.source_ref = g.external_id)) " +
            $"ORDER BY g.created_at DESC, g.id LIMIT {spec.Limit + 1} OFFSET {spec.Offset}",
            new { projectId = spec.ProjectId, source }, cancellationToken: ct))).ToList();
        var more = rows.Count > spec.Limit;
        var reviews = rows.Take(spec.Limit).Select(row => new UnlearnedReview(
            row.Id, WorkKeys.Item(row.ProjectKey, row.Number), ParseName<GitHubEvidenceKind>(row.Kind), row.AuthorLogin, row.Source, row.Priority, Utc(row.CreatedAt),
            row.Url, row.ExternalId, row.ReviewState, row.Body)).ToList();
        return new LessonsPage([], more, reviews);
    }

    private static async Task<IReadOnlyList<LessonRow>> LessonRowsAsync(MySqlConnection connection, IReadOnlyList<KnowledgeEntryRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(row => row.Id).ToList();
        var events = await ReadEventsAsync(connection, null, ids, ct);
        var contradicts = await ReadContradictionsAsync(connection, null, ids, activeOnly: true, ct);
        var challenges = await ReadChallengesAsync(connection, null, ids, openOnly: true, ct);
        var addressed = await ReadAddressedByAsync(connection, null, ids, ct);
        var served = await ReadServedAsync(connection, null, ids, ct);
        return rows.Select(row =>
        {
            var details = row.DetailsJson is { } json ? JsonSerializer.Deserialize<KnowledgeDetails>(json, WorkJson.Options) : null;
            var open = challenges[row.Id].Count();
            var by = addressed[row.Id].ToList();
            return new LessonRow(
                row.Id, row.Summary, row.Version,
                row.RuleState is { } state ? ParseName<LessonRuleState>(state) : LessonRuleState.Candidate,
                row.Priority ?? "normal",
                RecurrenceOf(events[row.Id]),
                Utc(row.CreatedAt),
                details?.AppliesWhen?.Activity,
                details?.Source?.Kind,
                row.RuleReason is { } reason ? ParseName<RuleReason>(reason) : null,
                row.RuleSince is { } since ? Utc(since) : null,
                served.TryGetValue(row.Id, out var found) ? found.Last : null,
                contradicts[row.Id].Any() ? true : null,
                open == 0 ? null : open,
                by.Count == 0 ? null : by);
        }).ToList();
    }

    // ---- review queue ------------------------------------------------------------------------------

    private const int MaxReviewCandidates = 500;

    public async Task<ReviewQueuePage> ReadReviewQueueAsync(Guid projectId, int offset, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var items = new List<ReviewItem>();

        // Open challenges of active rules, one item per rule (deduplicated).
        var challenged = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.project_id = @projectId AND e.status = 'active' AND " +
            $"EXISTS (SELECT 1 FROM lesson_challenges c WHERE c.entry_id = e.id AND c.status = 'open') ORDER BY e.id LIMIT {MaxReviewCandidates}",
            new { projectId }, cancellationToken: ct))).ToList();
        var challenges = await ReadChallengesAsync(connection, null, challenged.Select(row => row.Id).ToList(), openOnly: true, ct);
        foreach (var row in challenged)
        {
            var open = challenges[row.Id].ToList();
            var shown = open.Take(LessonLimits.MaxShownChallenges).ToList();
            items.Add(new ReviewItem(ReviewKinds.Challenge, ToRow(row), row.Priority ?? "normal", row.Recurrence ?? 1, open.Min(challenge => challenge.CreatedAt),
                Challenges: shown, MoreChallenges: open.Count > shown.Count ? open.Count - shown.Count : null));
        }

        // Lessons proposed for rule status.
        foreach (var row in await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.project_id = @projectId AND e.status = 'active' AND e.kind = 'lesson' AND e.rule_state = 'proposed' " +
            $"ORDER BY e.id LIMIT {MaxReviewCandidates}", new { projectId }, cancellationToken: ct)))
            items.Add(new ReviewItem(ReviewKinds.ProposedRule, ToRow(row), row.Priority ?? "normal", row.Recurrence ?? 1, Utc(row.UpdatedAt)));

        // Contradicting lessons, both active: one item per pair.
        var pairs = (await connection.QueryAsync<(Guid EntryId, Guid OtherId, DateTime CreatedAt)>(new CommandDefinition(
            "SELECT l.entry_id, l.other_id, l.created_at FROM lesson_links l JOIN knowledge_entries a ON a.id = l.entry_id JOIN knowledge_entries b ON b.id = l.other_id " +
            "WHERE a.project_id = @projectId AND l.relation = 'contradicts' AND l.entry_id < l.other_id AND a.status = 'active' AND b.status = 'active' " +
            $"ORDER BY l.created_at LIMIT {MaxReviewCandidates}", new { projectId }, cancellationToken: ct))).ToList();
        if (pairs.Count > 0)
        {
            var linked = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
                $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.id IN @ids",
                new { ids = pairs.SelectMany(pair => new[] { pair.EntryId, pair.OtherId }).Distinct().Select(id => id.ToString()).ToArray() }, cancellationToken: ct)))
                .ToDictionary(row => row.Id);
            foreach (var (entryId, otherId, createdAt) in pairs)
            {
                var a = linked[entryId];
                var b = linked[otherId];
                // The higher priority side leads.
                var (lead, other) = LessonSimilarity.PriorityRank(a.Priority) <= LessonSimilarity.PriorityRank(b.Priority) ? (a, b) : (b, a);
                items.Add(new ReviewItem(ReviewKinds.Contradiction, ToRow(lead), lead.Priority ?? "normal",
                    Math.Max(a.Recurrence ?? 1, b.Recurrence ?? 1), Utc(createdAt), Other: ToRow(other)));
            }
        }

        // Decisions an agent made on its own, not yet acknowledged (and not overturned).
        foreach (var row in await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.project_id = @projectId AND e.status = 'active' AND e.kind = 'decision' AND e.made_autonomously = 1 " +
            $"AND e.reviewed_at IS NULL ORDER BY e.created_at LIMIT {MaxReviewCandidates}", new { projectId }, cancellationToken: ct)))
            items.Add(new ReviewItem(ReviewKinds.AutonomousDecision, ToRow(row), "normal", 1, Utc(row.CreatedAt)));

        // Ranked by priority, then recurrence, then the oldest first.
        var ranked = items
            .OrderBy(item => LessonSimilarity.PriorityRank(item.Priority))
            .ThenByDescending(item => item.Recurrence)
            .ThenBy(item => item.Since)
            .ThenBy(item => item.Entry.Id)
            .ToList();
        return new ReviewQueuePage(ranked.Skip(offset).Take(limit).ToList(), ranked.Count > offset + limit);
    }
}
