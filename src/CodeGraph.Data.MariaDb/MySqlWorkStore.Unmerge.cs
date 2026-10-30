using System.Text.Json;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore
{
    private sealed class MergeLink
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
            "INSERT INTO lesson_merges (source_id, source_version, target_id, target_revision, project_id, snapshot_json, author, author_kind, author_session_id, created_at) " +
            "VALUES (@source, @version, @target, @targetRevision, @project, @snapshot, @author, @kind, @session, @now)",
            new { source = snapshot.Source.Id, version, target = snapshot.Target.Id, targetRevision = snapshot.Target.Revision + 1, project = snapshot.Source.ProjectId,
                snapshot = JsonSerializer.Serialize(snapshot, WorkJson.Options), author = actor.Name, kind = actor.Kind, session = actor.SessionId, now },
            transaction, cancellationToken: ct));
    }

    public Task<KnowledgeView> UnmergeLessonAsync(Guid entryId, long expectedVersion, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([LessonMergeLockName], async connection =>
        {
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
        var journal = await connection.QuerySingleOrDefaultAsync<(long SourceVersion, Guid TargetId, string SnapshotJson)?>(new CommandDefinition(
            "SELECT source_version, target_id, snapshot_json FROM lesson_merges WHERE source_id = @entryId AND unmerged_at IS NULL ORDER BY source_version DESC LIMIT 1 FOR UPDATE",
            new { entryId }, transaction, cancellationToken: ct))
            ?? throw WorkException.Invalid("This lesson has no reversible same_as merge. Legacy merges without a transfer journal cannot be unmerged. Nothing was changed.");
        var snapshot = JsonSerializer.Deserialize<LessonMergeSnapshot>(journal.SnapshotJson, WorkJson.Options)!;
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM lesson_merges WHERE target_id = @target AND unmerged_at IS NULL AND target_revision > @revision)",
            new { target = journal.TargetId, revision = snapshot.Target.Revision + 1 }, transaction, cancellationToken: ct)))
            throw new WorkException(WorkErrorCodes.Conflict, "Undo the target's later merges first. Nothing was changed.");
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
                "UPDATE lesson_events SET entry_id = @entryId, counted = @Counted WHERE correction_id = @CorrectionId AND entry_id = @targetId",
                new { entryId, targetId, moved.Counted, moved.CorrectionId }, transaction, cancellationToken: ct));
            if (restored != 1)
                throw new WorkException(WorkErrorCodes.Conflict, "A transferred correction moved again. Undo the later merge first. Nothing was changed.");
        }
        foreach (var id in snapshot.Challenges)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE lesson_challenges SET entry_id = @entryId WHERE id = @id AND entry_id = @targetId",
                new { entryId, targetId, id }, transaction, cancellationToken: ct));

        await RestoreMergeLinksAsync(connection, transaction, snapshot, ct);

        var remaining = (await ReadEventsAsync(connection, transaction, [targetId], ct))[targetId].ToList();
        var original = snapshot.TargetEvents.ToDictionary(e => e.CorrectionId);
        var seen = snapshot.TargetEvents.Where(e => e.Counted).Select(e => e.EventHash).ToHashSet();
        foreach (var row in remaining)
        {
            var counted = original.TryGetValue(row.CorrectionId, out var baseline) ? baseline.Counted : seen.Add(row.EventHash);
            await connection.ExecuteAsync(new CommandDefinition("UPDATE lesson_events SET counted = @counted WHERE correction_id = @id",
                new { counted, id = row.CorrectionId }, transaction, cancellationToken: ct));
        }
        var details = MergeDetails(target.DetailsJson);
        var appliesWhen = MergeDetails(snapshot.Target.DetailsJson).AppliesWhen;
        var priority = snapshot.Target.Priority ?? "normal";
        foreach (var row in remaining.Where(e => !original.ContainsKey(e.CorrectionId)))
        {
            appliesWhen = UnionAppliesWhen(appliesWhen,
                row.AppliesWhenJson is null ? null : JsonSerializer.Deserialize<AppliesWhenView>(row.AppliesWhenJson, WorkJson.Options));
            priority = BestPriority(priority, row.SourceKind == "prompt" ? "highest" : row.SourceKind is "review" or "bot_review" ? "high" : "normal");
        }
        // Explicit edits made afterwards are owned by their editor, not by this merge.
        var edits = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT action FROM knowledge_revisions WHERE entry_id = @targetId AND revision > @revision",
            new { targetId, revision = snapshot.Target.Revision + 1 }, transaction, cancellationToken: ct))).ToHashSet();
        var applicabilityEdited = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM knowledge_revisions r JOIN knowledge_revisions previous ON previous.entry_id = r.entry_id AND previous.revision = r.revision - 1 " +
            "WHERE r.entry_id = @targetId AND r.revision > @revision AND r.action IN ('revised', 'amended') " +
            "AND NOT (JSON_EXTRACT(r.snapshot_json, '$.details.applies_when') <=> JSON_EXTRACT(previous.snapshot_json, '$.details.applies_when')))",
            new { targetId, revision = snapshot.Target.Revision + 1 }, transaction, cancellationToken: ct));
        if (!applicabilityEdited)
        {
            details = details with { AppliesWhen = appliesWhen };
            await connection.ExecuteAsync(new CommandDefinition("UPDATE knowledge_entries SET details_json = @details, activities = @activities WHERE id = @targetId",
                new { details = DetailsJson(details), activities = Activities(details), targetId }, transaction, cancellationToken: ct));
        }
        if (!edits.Overlaps(["approved", "rejected", "proposed", "amended"]))
        {
            await RestoreRuleAsync(connection, transaction, snapshot.Target, ct);
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET recurrence = (SELECT COUNT(*) FROM lesson_events WHERE entry_id = @targetId AND counted = 1), priority = @priority WHERE id = @targetId",
            new { targetId, priority }, transaction, cancellationToken: ct));
        // Re-evaluate recurrence promotion after the count was restored.
        if (!edits.Overlaps(["approved", "rejected", "proposed", "amended"]))
            await PromoteIfDueAsync(connection, transaction, targetId, now, remaining.Any(e => !original.ContainsKey(e.CorrectionId) && e.SourceKind == "prompt"), ct);
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
