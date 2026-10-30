using System.Text.Json;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore
{
    private sealed record MergeLink
    {
        public Guid EntryId { get; init; }
        public Guid OtherId { get; init; }
        public string Relation { get; init; } = "contradicts";
        public string Author { get; init; } = "";
        public string AuthorKind { get; init; } = "";
        public string? AuthorSessionId { get; init; }
        public string? AuthorHarness { get; init; }
        public string? AuthorModel { get; init; }
        public string? AuthorHost { get; init; }
        public DateTime CreatedAt { get; init; }
        public Guid? AssertionId { get; init; }
    }

    private sealed record LessonMergeSnapshot(KnowledgeEntryRow Source, KnowledgeEntryRow Target,
        IReadOnlyList<EventRow> SourceEvents, IReadOnlyList<EventRow> TargetEvents,
        IReadOnlyList<MergeLink> SourceLinks, IReadOnlyList<MergeLink> TargetLinks, IReadOnlyList<Guid> Challenges,
        IReadOnlyList<MergeLink>? ImportedLinks = null);

    private static KnowledgeDetails MergeDetails(string? json) => json is null ? new()
        : JsonSerializer.Deserialize<KnowledgeDetails>(json, WorkJson.Options) ?? new();

    private static async Task<LessonMergeSnapshot> CaptureLessonMergeAsync(MySqlConnection connection, MySqlTransaction transaction,
        KnowledgeEntryRow source, KnowledgeEntryRow target, CancellationToken ct)
    {
        var events = await ReadEventsAsync(connection, transaction, [source.Id, target.Id], ct);
        var links = (await connection.QueryAsync<MergeLink>(new CommandDefinition(
            "SELECT * FROM lesson_links WHERE entry_id IN @ids OR other_id IN @ids",
            new { ids = new[] { source.Id.ToString(), target.Id.ToString() } }, transaction, cancellationToken: ct))).ToList();
        var challenges = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM lesson_challenges WHERE entry_id = @id AND status = 'open'",
            new { id = source.Id }, transaction, cancellationToken: ct))).ToList();
        return new(source, target, events[source.Id].ToList(), events[target.Id].ToList(),
            links.Where(l => l.EntryId == source.Id || l.OtherId == source.Id).ToList(),
            links.Where(l => l.EntryId == target.Id || l.OtherId == target.Id).ToList(), challenges);
    }

    private static async Task SaveLessonMergeAsync(MySqlConnection connection, MySqlTransaction transaction,
        LessonMergeSnapshot snapshot, long version, WorkActor actor, DateTime now, CancellationToken ct)
    {
        var prior = snapshot.TargetLinks.ToDictionary(l => (l.EntryId, l.OtherId, l.Relation));
        var after = (await connection.QueryAsync<MergeLink>(new CommandDefinition(
            "SELECT * FROM lesson_links WHERE entry_id = @id OR other_id = @id",
            new { id = snapshot.Target.Id }, transaction, cancellationToken: ct))).ToList();
        snapshot = snapshot with { ImportedLinks = after.Where(l => !prior.TryGetValue((l.EntryId, l.OtherId, l.Relation), out var old) || old.AssertionId != l.AssertionId).ToList() };
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO lesson_merges (source_id, source_version, target_id, target_revision, project_id, snapshot_json, inverse_json, author, author_kind, author_session_id, created_at) " +
            "VALUES (@source, @version, @target, @targetRevision, @project, @snapshot, @snapshot, @author, @kind, @session, @now)",
            new { source = snapshot.Source.Id, version, target = snapshot.Target.Id, targetRevision = snapshot.Target.Revision + 1, project = snapshot.Source.ProjectId,
                snapshot = JsonSerializer.Serialize(snapshot, WorkJson.Options), author = actor.Name, kind = actor.Kind, session = actor.SessionId, now },
            transaction, cancellationToken: ct));
    }

    public Task<KnowledgeView> UnmergeLessonAsync(Guid entryId, long expectedVersion, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([LessonMergeLockName], async connection =>
        {
            DreamPolicy.Refuse(actor, "unmerge lessons directly");
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var result = await UnmergeLessonCoreAsync(connection, transaction, entryId, expectedVersion, actor, Now(), ct);
            await transaction.CommitAsync(ct);
            return result;
        }, ct);

    // Shared with run revert: the caller holds the named lesson-merge lock and commits.
    private async Task<KnowledgeView> UnmergeLessonCoreAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid entryId, long expectedVersion, WorkActor actor, DateTime now, CancellationToken ct)
    {
        var projectId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT project_id FROM knowledge_entries WHERE id = @entryId", new { entryId }, transaction, cancellationToken: ct))
            ?? throw WorkException.NotFound($"There is no knowledge entry {entryId}.");
        DreamPolicy.RequireProject(actor, projectId);
        DreamPolicy.RequireWrite(actor, "unmerge");
        await HoldProjectAsync(connection, transaction, projectId, ct);
        var currentSource = (await ReadKnowledgeRowAsync(connection, transaction, entryId, ct))!;
        if (currentSource.Version != expectedVersion)
            throw new WorkException(WorkErrorCodes.Conflict, "The lesson changed after it was read. Read it again and use its current version. Nothing was changed.",
                await LoadKnowledgeViewAsync(connection, transaction, entryId, false, ct));
        var journal = await connection.QuerySingleOrDefaultAsync<(long SourceVersion, Guid TargetId, string SnapshotJson)?>(new CommandDefinition(
            "SELECT source_version, target_id, COALESCE(inverse_json, snapshot_json) AS snapshot_json FROM lesson_merges WHERE source_id = @entryId AND unmerged_at IS NULL ORDER BY source_version DESC LIMIT 1 FOR UPDATE",
            new { entryId }, transaction, cancellationToken: ct))
            ?? throw WorkException.Invalid("This lesson has no reversible same_as merge. Legacy merges without a transfer journal cannot be unmerged. Nothing was changed.");
        var snapshot = JsonSerializer.Deserialize<LessonMergeSnapshot>(journal.SnapshotJson, WorkJson.Options)!;
        var targetId = journal.TargetId;
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT id FROM knowledge_entries WHERE id IN @ids ORDER BY id FOR UPDATE",
            new { ids = new[] { entryId.ToString(), targetId.ToString() } }, transaction, cancellationToken: ct));
        var source = (await ReadKnowledgeRowAsync(connection, transaction, entryId, ct))!;
        var target = (await ReadKnowledgeRowAsync(connection, transaction, targetId, ct))!;
        if (source.Version != expectedVersion || source.Status != "superseded" || source.SupersededById != targetId || target.Status != "active")
            throw new WorkException(WorkErrorCodes.Conflict,
                "The merge changed after it was read, or its target is no longer active. Read both entries again and undo later merges first. Nothing was changed.",
                await LoadKnowledgeViewAsync(connection, transaction, entryId, false, ct));

        foreach (var moved in snapshot.SourceEvents)
        {
            var restored = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE lesson_events SET entry_id = @entryId, counted = @Counted, arrival_revision = @ArrivalRevision WHERE correction_id = @CorrectionId AND entry_id = @targetId",
                new { entryId, targetId, moved.Counted, moved.ArrivalRevision, moved.CorrectionId }, transaction, cancellationToken: ct));
            if (restored != 1)
                throw new WorkException(WorkErrorCodes.Conflict, "A transferred correction moved again. Undo the later merge first. Nothing was changed.");
        }
        foreach (var id in snapshot.Challenges)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE lesson_challenges SET entry_id = @entryId WHERE id = @id AND entry_id = @targetId",
                new { entryId, targetId, id }, transaction, cancellationToken: ct));

        await RestoreMergeLinksAsync(connection, transaction, snapshot, ct);

        var plans = (await connection.QueryAsync<MergePlanRow>(new CommandDefinition(
            "SELECT source_id, source_version, target_revision, COALESCE(inverse_json, snapshot_json) AS snapshot_json, created_at FROM lesson_merges " +
            "WHERE target_id = @targetId AND unmerged_at IS NULL AND NOT (source_id = @entryId AND source_version = @version) ORDER BY target_revision",
            new { targetId, entryId, version = journal.SourceVersion }, transaction, cancellationToken: ct))).ToList();
        var remaining = RecountMergeEvents(snapshot, (await ReadEventsAsync(connection, transaction, [targetId], ct))[targetId], plans, target.Revision);
        foreach (var row in remaining)
            await connection.ExecuteAsync(new CommandDefinition("UPDATE lesson_events SET counted = @Counted WHERE correction_id = @CorrectionId",
                row, transaction, cancellationToken: ct));
        var rebuilt = await RebuildMergeTargetAsync(connection, transaction, snapshot, target, remaining, plans, now, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET details_json = @DetailsJson, activities = @activities, recurrence = @Recurrence, priority = @Priority, " +
            "rule_state = @RuleState, rule_reason = @RuleReason, rule_since = @RuleSince, rejected_reason = @RejectedReason WHERE id = @Id",
            new { rebuilt.Id, rebuilt.DetailsJson, activities = Activities(MergeDetails(rebuilt.DetailsJson)), rebuilt.Recurrence, rebuilt.Priority,
                rebuilt.RuleState, rebuilt.RuleReason, rebuilt.RuleSince, rebuilt.RejectedReason }, transaction, cancellationToken: ct));

        // Rebase only active inverse plans. Their immutable audit snapshots remain the original
        // evidence of what the merge did. This makes selected unmerge commute with independent
        // later merges: a later inverse cannot re-import this source's events, scope or links.
        foreach (var plan in plans.Where(p => p.TargetRevision > snapshot.Target.Revision + 1))
        {
            var inverse = JsonSerializer.Deserialize<LessonMergeSnapshot>(plan.SnapshotJson, WorkJson.Options)!;
            var rebasedEvents = RecountMergeEvents(snapshot, inverse.TargetEvents, plans, inverse.Target.Revision);
            var rebasedTarget = await RebuildMergeTargetAsync(connection, transaction, snapshot, inverse.Target, rebasedEvents, plans, plan.CreatedAt, ct);
            inverse = inverse with { Target = rebasedTarget, TargetEvents = rebasedEvents,
                TargetLinks = RebaseMergeLinks(snapshot, inverse.TargetLinks), SourceLinks = RebaseMergeLinks(snapshot, inverse.SourceLinks) };
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE lesson_merges SET inverse_json = @inverse WHERE source_id = @SourceId AND source_version = @SourceVersion",
                new { inverse = JsonSerializer.Serialize(inverse, WorkJson.Options), plan.SourceId, plan.SourceVersion }, transaction, cancellationToken: ct));
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET status = 'active', superseded_by_id = NULL, status_reason = NULL, recurrence = (SELECT COUNT(*) FROM lesson_events WHERE entry_id = @entryId AND counted = 1) WHERE id = @entryId",
            new { entryId }, transaction, cancellationToken: ct));
        await RestoreRuleAsync(connection, transaction, snapshot.Source, ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE knowledge_entries SET priority = @Priority WHERE id = @Id",
            snapshot.Source, transaction, cancellationToken: ct));
        await BumpLessonAsync(connection, transaction, source, "unmerged", actor, now, ct);
        await BumpLessonAsync(connection, transaction, target, "unmerged_from", actor, now, ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE lesson_merges SET unmerged_at = @now WHERE source_id = @entryId AND source_version = @version",
            new { now, entryId, version = journal.SourceVersion }, transaction, cancellationToken: ct));
        return (await LoadKnowledgeViewAsync(connection, transaction, entryId, false, ct))!;
    }

    private sealed class MergePlanRow
    {
        public Guid SourceId { get; init; }
        public long SourceVersion { get; init; }
        public int TargetRevision { get; init; }
        public string SnapshotJson { get; init; } = "";
        public DateTime CreatedAt { get; init; }
    }

    private static List<EventRow> RecountMergeEvents(LessonMergeSnapshot removed, IEnumerable<EventRow> rows, IReadOnlyList<MergePlanRow> plans, int throughRevision)
    {
        var imported = removed.SourceEvents.Select(e => e.CorrectionId).ToHashSet();
        var baseline = removed.TargetEvents.ToDictionary(e => e.CorrectionId);
        var sourceFlags = plans.Where(p => p.TargetRevision <= throughRevision)
            .SelectMany(p => JsonSerializer.Deserialize<LessonMergeSnapshot>(p.SnapshotJson, WorkJson.Options)!.SourceEvents)
            .ToDictionary(e => e.CorrectionId, e => e.Counted);
        var remaining = rows.Where(e => !imported.Contains(e.CorrectionId)).OrderBy(e => baseline.ContainsKey(e.CorrectionId) ? 0 : 1)
            .ThenBy(e => e.ArrivalRevision ?? int.MaxValue).ThenBy(e => e.CreatedAt).ThenBy(e => e.CorrectionId).ToList();
        var hashes = new HashSet<string>();
        var keys = new HashSet<string>();
        var result = new List<EventRow>();
        foreach (var row in remaining)
        {
            var seen = LessonEventSeen(row.EventKey, row.EventHash, hashes, keys);
            var counted = baseline.TryGetValue(row.CorrectionId, out var old) ? old.Counted
                : sourceFlags.TryGetValue(row.CorrectionId, out var sourceCounted) ? sourceCounted && !seen : !seen;
            if (counted) { hashes.Add(row.EventHash); keys.Add(row.EventKey); }
            result.Add(row with { Counted = counted });
        }
        return result;
    }

    private static IReadOnlyList<MergeLink> RebaseMergeLinks(LessonMergeSnapshot removed, IReadOnlyList<MergeLink> links)
    {
        var imported = (removed.ImportedLinks ?? []).ToDictionary(l => (l.EntryId, l.OtherId, l.Relation));
        var baseline = removed.TargetLinks.ToDictionary(l => (l.EntryId, l.OtherId, l.Relation));
        var result = new List<MergeLink>();
        foreach (var link in links)
        {
            var key = (link.EntryId, link.OtherId, link.Relation);
            if (!imported.TryGetValue(key, out var added) || added.AssertionId != link.AssertionId) result.Add(link);
            else if (baseline.TryGetValue(key, out var old)) result.Add(old);
        }
        return result;
    }

    private async Task<KnowledgeEntryRow> RebuildMergeTargetAsync(MySqlConnection connection, MySqlTransaction transaction,
        LessonMergeSnapshot removed, KnowledgeEntryRow state, IReadOnlyList<EventRow> events, IReadOnlyList<MergePlanRow> plans, DateTime now, CancellationToken ct)
    {
        var baselineIds = removed.TargetEvents.Select(e => e.CorrectionId).ToHashSet();
        var later = plans.Where(p => p.TargetRevision > removed.Target.Revision + 1 && p.TargetRevision <= state.Revision)
            .Select(p => (Plan: p, Snapshot: JsonSerializer.Deserialize<LessonMergeSnapshot>(p.SnapshotJson, WorkJson.Options)!)).ToList();
        var imports = later.SelectMany(p => p.Snapshot.SourceEvents.Select(e => (e.CorrectionId, p.Plan.CreatedAt))).ToDictionary(p => p.CorrectionId, p => p.CreatedAt);
        var additions = events.Where(e => !baselineIds.Contains(e.CorrectionId)).ToList();
        var applicabilityEdited = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM knowledge_revisions r JOIN knowledge_revisions previous ON previous.entry_id = r.entry_id AND previous.revision = r.revision - 1 " +
            "WHERE r.entry_id = @id AND r.revision > @start AND r.revision <= @end AND r.action IN ('revised', 'amended') " +
            "AND NOT (JSON_EXTRACT(r.snapshot_json, '$.details.applies_when') <=> JSON_EXTRACT(previous.snapshot_json, '$.details.applies_when')))",
            new { id = state.Id, start = removed.Target.Revision + 1, end = state.Revision }, transaction, cancellationToken: ct));
        var details = MergeDetails(state.DetailsJson);
        if (!applicabilityEdited)
        {
            var applies = MergeDetails(removed.Target.DetailsJson).AppliesWhen;
            foreach (var addition in additions.Where(e => !imports.ContainsKey(e.CorrectionId)))
                applies = UnionAppliesWhen(applies, addition.AppliesWhenJson is null ? null : JsonSerializer.Deserialize<AppliesWhenView>(addition.AppliesWhenJson, WorkJson.Options));
            foreach (var merge in later)
                applies = UnionAppliesWhen(applies, MergeDetails(merge.Snapshot.Source.DetailsJson).AppliesWhen);
            details = details with { AppliesWhen = applies };
        }
        var priority = removed.Target.Priority ?? "normal";
        foreach (var addition in additions.Where(e => e.Counted))
            priority = BestPriority(priority, addition.SourceKind == "prompt" ? "highest" : addition.SourceKind is "review" or "bot_review" ? "high" : "normal");
        foreach (var merge in later) priority = BestPriority(priority, merge.Snapshot.Source.Priority);
        var rebuilt = state with { DetailsJson = DetailsJson(details), Priority = priority, Recurrence = events.Count(e => e.Counted) };
        var ruleEdited = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM knowledge_revisions WHERE entry_id = @id AND revision > @start AND revision <= @end AND action IN ('approved','rejected','proposed','amended'))",
            new { id = state.Id, start = removed.Target.Revision + 1, end = state.Revision }, transaction, cancellationToken: ct));
        if (ruleEdited) return rebuilt;
        rebuilt = rebuilt with { RuleState = removed.Target.RuleState, RuleReason = removed.Target.RuleReason,
            RuleSince = removed.Target.RuleSince, RejectedReason = removed.Target.RejectedReason };
        var scopeEdits = (await connection.QueryAsync<(int Revision, DateTime CreatedAt, string? AppliesWhenJson)>(new CommandDefinition(
            "SELECT r.revision, r.created_at, JSON_EXTRACT(r.snapshot_json, '$.details.applies_when') AS applies_when_json " +
            "FROM knowledge_revisions r JOIN knowledge_revisions previous ON previous.entry_id = r.entry_id AND previous.revision = r.revision - 1 " +
            "WHERE r.entry_id = @id AND r.revision > @start AND r.revision <= @end AND r.action = 'revised' " +
            "AND NOT (JSON_EXTRACT(r.snapshot_json, '$.details.applies_when') <=> JSON_EXTRACT(previous.snapshot_json, '$.details.applies_when'))",
            new { id = state.Id, start = removed.Target.Revision + 1, end = state.Revision }, transaction, cancellationToken: ct))).ToList();
        var steps = additions.Where(e => !imports.ContainsKey(e.CorrectionId)).Select(e => new MergeReplayStep(e.ArrivalRevision ?? int.MaxValue, e.CreatedAt, e, null, null))
            .Concat(later.Select(p => new MergeReplayStep(p.Plan.TargetRevision, p.Plan.CreatedAt, null, p.Snapshot, null)))
            .Concat(scopeEdits.Select(e => new MergeReplayStep(e.Revision, e.CreatedAt, null, null,
                e.AppliesWhenJson is null ? new AppliesWhenView() : JsonSerializer.Deserialize<AppliesWhenView>(e.AppliesWhenJson, WorkJson.Options) ?? new AppliesWhenView())))
            .OrderBy(e => e.Revision).ThenBy(e => e.At).ToList();
        var appliesNow = MergeDetails(removed.Target.DetailsJson).AppliesWhen;
        var count = events.Count(e => baselineIds.Contains(e.CorrectionId) && e.Counted);
        var sourcePrompt = MergeDetails(removed.Target.DetailsJson).Source?.Kind == CorrectionSourceKind.Prompt;
        void Promote(bool promptEvent, DateTime at)
        {
            if (rebuilt.IsRule || !HasActivity(new KnowledgeDetails(AppliesWhen: appliesNow))) return;
            var reason = promptEvent || sourcePrompt && rebuilt.RuleState != "rejected" ? "human_prompt"
                : rebuilt.RuleState != "rejected" && count >= ruleThreshold ? "recurrence" : null;
            if (reason is not null) rebuilt = rebuilt with { RuleState = "rule", RuleReason = reason, RuleSince = at, RejectedReason = null };
        }
        foreach (var step in steps)
        {
            if (step.Correction is { } correction)
            {
                appliesNow = UnionAppliesWhen(appliesNow, correction.AppliesWhenJson is null ? null : JsonSerializer.Deserialize<AppliesWhenView>(correction.AppliesWhenJson, WorkJson.Options));
                if (correction.Counted) count++;
                Promote(correction.Counted && correction.SourceKind == "prompt", step.At);
            }
            else if (step.Merge is { } merge)
            {
                appliesNow = UnionAppliesWhen(appliesNow, MergeDetails(merge.Source.DetailsJson).AppliesWhen);
                var ids = merge.SourceEvents.Select(e => e.CorrectionId).ToHashSet();
                count += events.Count(e => ids.Contains(e.CorrectionId) && e.Counted);
                if (merge.Source.IsRule && !rebuilt.IsRule)
                    rebuilt = rebuilt with { RuleState = "rule", RuleReason = merge.Source.RuleReason,
                        RuleSince = merge.Source.RuleSince ?? step.At, RejectedReason = null };
                else Promote(false, step.At);
            }
            else { appliesNow = step.Scope; Promote(false, step.At); }
        }
        return rebuilt;
    }

    private sealed record MergeReplayStep(int Revision, DateTime At, EventRow? Correction, LessonMergeSnapshot? Merge, AppliesWhenView? Scope);

    private static Task RestoreRuleAsync(MySqlConnection connection, MySqlTransaction transaction, KnowledgeEntryRow row, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("UPDATE knowledge_entries SET rule_state = @RuleState, rule_reason = @RuleReason, rule_since = @RuleSince, rejected_reason = @RejectedReason WHERE id = @Id",
            row, transaction, cancellationToken: ct));

    private static async Task RestoreMergeLinksAsync(MySqlConnection connection, MySqlTransaction transaction, LessonMergeSnapshot snapshot, CancellationToken ct)
    {
        var prior = snapshot.TargetLinks.ToDictionary(l => (l.EntryId, l.OtherId, l.Relation));
        foreach (var link in snapshot.ImportedLinks ?? [])
        {
            if (prior.TryGetValue((link.EntryId, link.OtherId, link.Relation), out var original))
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE lesson_links SET assertion_id = @original WHERE entry_id = @EntryId AND other_id = @OtherId AND relation = @Relation AND assertion_id = @AssertionId",
                    new { original = original.AssertionId, link.EntryId, link.OtherId, link.Relation, link.AssertionId }, transaction, cancellationToken: ct));
            else
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM lesson_links WHERE entry_id = @EntryId AND other_id = @OtherId AND relation = @Relation AND assertion_id = @AssertionId",
                    link, transaction, cancellationToken: ct));
        }
        foreach (var link in snapshot.SourceLinks)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT IGNORE INTO lesson_links (entry_id, other_id, relation, author, author_kind, author_session_id, author_harness, author_model, author_host, created_at, assertion_id) " +
                "VALUES (@EntryId, @OtherId, @Relation, @Author, @AuthorKind, @AuthorSessionId, @AuthorHarness, @AuthorModel, @AuthorHost, @CreatedAt, @AssertionId)",
                link, transaction, cancellationToken: ct));
        }
    }
}
