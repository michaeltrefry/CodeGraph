using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// The project knowledge store (sc-27869, design §5.3). Part of the work store so that the save-point
// fields of update_work and end_session are written by InsertKnowledgeAsync inside the work write's
// own transaction: the item change and its decisions commit together or not at all.
//
// Freshness is computed from data the indexer already keeps: knowledge_entry_files holds the
// file_hashes.content_hash of every indexed file an entry was verified against, and an entry reads
// possibly_stale while any of them now has another hash or is no longer indexed. Nothing is crawled,
// and reads never write: a file not indexed when its entry was recorded gets its baseline after
// commit, best effort, from whichever of the index write and the knowledge write commits later
// (KnowledgeBaselines, called by MySqlGraphStore and by the wrappers below).
//
// Lock order (the store's, see MySqlWorkStore): project, feature, items, resources and holds, then
// knowledge rows, then the change-feed lock. A write that stores knowledge holds the project first
// (save points included) and inserts its entries after its item, resource and hold writes.
//
// Change feed: record (entry, supersede, revise, retract, verify) writes no work_changes entry; the
// feed (E7) covers projects, features and items, and save points written by update_work on one of
// them appear in that entity's entry as a "knowledge" change.
public sealed partial class MySqlWorkStore : IKnowledgeStore
{
    private const string KnowledgeOperation = "knowledge";
    private const string ImportOperation = "knowledge_import";
    private const int RecallBatch = 200;
    private const int MaxRecallScan = 5_000;
    private const int MaxRelevantCandidates = 500;

    // An entry is possibly stale while a file it was verified against has another indexed hash, or
    // is no longer indexed. A file not yet indexed at verification (NULL hash) is not counted.
    private const string StaleCondition =
        "EXISTS (SELECT 1 FROM knowledge_entry_files kf LEFT JOIN file_hashes h ON h.repository = kf.repository_name AND h.rel_path = kf.path " +
        "WHERE kf.entry_id = e.id AND kf.content_hash IS NOT NULL AND (h.content_hash IS NULL OR h.content_hash <> kf.content_hash))";

    private const string KnowledgeColumns =
        "e.id, e.project_id, p.project_key, e.kind, e.status, e.summary, e.body, e.details_json, e.confidence, e.made_autonomously, " +
        "e.item_id, wi.number AS item_number, wi.title AS item_title, e.feature_id, wf.number AS feature_number, wf.title AS feature_title, " +
        "e.supersedes_id, e.superseded_by_id, e.status_reason, e.source_file, e.verified_commit, e.verified_at, e.revision, e.version, " +
        "e.author, e.author_kind, e.author_session_id, e.author_harness, e.author_model, e.author_host, e.created_at, e.updated_at, " +
        "e.rule_state, e.rule_reason, e.rule_since, e.rejected_reason, e.recurrence, e.priority, e.reviewed_at, " +
        StaleCondition + " AS stale";

    private const string KnowledgeFrom =
        "FROM knowledge_entries e JOIN work_projects p ON p.id = e.project_id " +
        "LEFT JOIN work_items wi ON wi.id = e.item_id LEFT JOIN work_features wf ON wf.id = e.feature_id";

    private sealed record KnowledgeEntryRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string ProjectKey { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Status { get; init; } = "";
        public string Summary { get; init; } = "";
        public string? Body { get; init; }
        public string? DetailsJson { get; init; }
        public string Confidence { get; init; } = "";
        public bool? MadeAutonomously { get; init; }
        public Guid? ItemId { get; init; }
        public int? ItemNumber { get; init; }
        public string? ItemTitle { get; init; }
        public Guid? FeatureId { get; init; }
        public int? FeatureNumber { get; init; }
        public string? FeatureTitle { get; init; }
        public Guid? SupersedesId { get; init; }
        public Guid? SupersededById { get; init; }
        public string? StatusReason { get; init; }
        public string? SourceFile { get; init; }
        public string? VerifiedCommit { get; init; }
        public DateTime VerifiedAt { get; init; }
        public int Revision { get; init; }
        public long Version { get; init; }
        public string Author { get; init; } = "";
        public string AuthorKind { get; init; } = "";
        public string? AuthorSessionId { get; init; }
        public string? AuthorHarness { get; init; }
        public string? AuthorModel { get; init; }
        public string? AuthorHost { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public long Stale { get; init; }

        // Lessons (S9).
        public string? RuleState { get; init; }
        public string? RuleReason { get; init; }
        public DateTime? RuleSince { get; init; }
        public string? RejectedReason { get; init; }
        public int? Recurrence { get; init; }
        public string? Priority { get; init; }

        // An autonomous decision acknowledged from the review queue (S9).
        public DateTime? ReviewedAt { get; init; }

        public bool IsRule => RuleState == "rule";

        // Set only by the briefing's query of entries without paths: 1 when the entry names no repository.
        public long ProjectWide { get; init; }

        // Set only by the repeated-end check.
        public string? SummaryHash { get; init; }

        public bool PossiblyStale => Stale != 0;

        public string? ItemKey => ItemNumber is { } number ? WorkKeys.Item(ProjectKey, number) : null;
    }

    private sealed record NodeRef(long Id, string Repository, string QualifiedName, string? FilePath);

    // ---- Post-commit baselines ----------------------------------------------------------------
    //
    // Every write that stores knowledge (record, revise/verify/retract, save points, import) is the
    // *CoreAsync transaction followed, once it has committed, by the best-effort adoption of the
    // pending file baselines of what it stored, from file hashes committed by then (see
    // KnowledgeBaselines for why this and the indexer's own adoption cannot both miss).

    private Task AdoptPendingAsync(IEnumerable<Guid>? entryIds, CancellationToken ct) =>
        KnowledgeBaselines.AdoptPendingAsync(options.ConnectionString, entryIds?.Distinct().ToList() ?? [], log, ct);

    private static IEnumerable<Guid>? IdsOf(IReadOnlyList<KnowledgeSpec>? specs) => specs?.Select(spec => spec.Id);

    public async Task<ProjectView> UpdateProjectAsync(Guid projectId, long expectedVersion, ProjectPatchSpec patch, WorkActor actor, CancellationToken ct = default)
    {
        DreamPolicy.Refuse(actor, "UpdateProjectAsync");
        var result = await UpdateProjectCoreAsync(projectId, expectedVersion, patch, actor, ct);
        await AdoptPendingAsync(IdsOf(patch.Knowledge), ct);
        return result;
    }

    public async Task<FeatureView> UpdateFeatureAsync(Guid featureId, long expectedVersion, FeaturePatchSpec patch, WorkActor actor, CancellationToken ct = default)
    {
        DreamPolicy.Refuse(actor, "UpdateFeatureAsync");
        var result = await UpdateFeatureCoreAsync(featureId, expectedVersion, patch, actor, ct);
        await AdoptPendingAsync(IdsOf(patch.Knowledge), ct);
        return result;
    }

    public async Task<ItemView> UpdateItemAsync(Guid itemId, long expectedVersion, ItemPatchSpec patch, WorkActor actor, CancellationToken ct = default)
    {
        DreamPolicy.Refuse(actor, "UpdateItemAsync");
        var result = await UpdateItemCoreAsync(itemId, expectedVersion, patch, actor, ct);
        await AdoptPendingAsync(IdsOf(patch.Knowledge), ct);
        return result;
    }

    public async Task<EndSessionResult> EndSessionAsync(Guid sessionId, Guid? itemId, HandoffSpec? handoff, IReadOnlyList<KnowledgeSpec>? knowledge, WorkActor actor, CancellationToken ct = default)
    {
        DreamPolicy.Refuse(actor, "EndSessionAsync");
        var result = await EndSessionCoreAsync(sessionId, itemId, handoff, knowledge, actor, ct);
        await AdoptPendingAsync(IdsOf(knowledge), ct);
        return result;
    }

    public async Task<RecordResult> RecordKnowledgeAsync(Guid projectId, KnowledgeSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default)
    {
        RequireDreamRecord(actor, projectId, spec);
        var result = await RecordKnowledgeCoreAsync(projectId, spec, actor, keyHash, ct);
        if (!result.Created)
            return result;
        await AdoptPendingAsync([result.Entry.Id], ct);
        // Answer the entry with the baselines just adopted.
        return result with { Entry = await GetKnowledgeAsync(result.Entry.Id, withRevisions: false, ct) ?? result.Entry };
    }

    private async Task<KnowledgeView> ChangeKnowledgeAsync(Guid entryId, long expectedVersion, string action, WorkActor actor,
        Func<MySqlConnection, MySqlTransaction, KnowledgeEntryRow, DateTime, Task> change, CancellationToken ct)
    {
        if (DreamPolicy.IsDreamer(actor))
        {
            var current = await GetKnowledgeAsync(entryId, false, ct) ?? throw WorkException.NotFound("No such dreamer knowledge entry.");
            DreamPolicy.RequireProject(actor, current.ProjectId);
            DreamPolicy.RequireWrite(actor, action);
            if (action == "retracted") DreamPolicy.Refuse(actor, "retract knowledge");
        }
        var view = await ChangeKnowledgeCoreAsync(entryId, expectedVersion, action, actor, change, ct);
        await AdoptPendingAsync([entryId], ct);
        return await GetKnowledgeAsync(entryId, withRevisions: false, ct) ?? view;
    }

    public async Task<ImportKnowledgeResult> ImportKnowledgeAsync(Guid projectId, IReadOnlyList<ImportCandidateSpec> candidates, string previewToken,
        IReadOnlyList<int>? accept, string? keyHash, WorkActor actor, CancellationToken ct = default)
    {
        DreamPolicy.Refuse(actor, "ImportKnowledgeAsync");
        var result = await ImportKnowledgeCoreAsync(projectId, candidates, previewToken, accept, keyHash, actor, ct);
        if (result.Stored)
            await AdoptPendingAsync(result.Candidates.Select(candidate => candidate.Entry?.Id).OfType<Guid>(), ct);
        return result;
    }

    // ---- Writes ------------------------------------------------------------------------------

    private Task<RecordResult> RecordKnowledgeCoreAsync(Guid projectId, KnowledgeSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // Holding the project's row makes a keyed repeat find the first call's entry.
            var project = await HoldProjectAsync(connection, transaction, projectId, ct);
            if (keyHash is not null && await FindKeyAsync(connection, transaction, projectId, KnowledgeOperation, keyHash, ct) is { EntityId: { } existing })
            {
                var earlier = (await LoadKnowledgeViewAsync(connection, transaction, existing, withRevisions: false, ct))!;
                await transaction.CommitAsync(ct);
                return new RecordResult(false, earlier);
            }

            var dreamBefore = await CaptureDreamBeforeAsync(connection, transaction, actor, [spec.Id], ct);
            var outcome = await InsertKnowledgeAsync(connection, transaction, project, [spec], null, null, null, actor, now, ct);
            // A lesson merged into an existing one (same_as, or an exact normalized match) stores no new entry.
            var effective = outcome.EntryOf(spec.Id);
            if (keyHash is not null)
                await InsertKeyAsync(connection, transaction, projectId, KnowledgeOperation, keyHash, effective, null, now, ct);

            var view = (await LoadKnowledgeViewAsync(connection, transaction, effective, withRevisions: false, ct))!;
            KnowledgeRow? superseded = null;
            if (outcome.Superseded is { } old)
                superseded = ToRow((await ReadKnowledgeRowAsync(connection, transaction, old, ct))!);
            await JournalDreamAfterAsync(connection, transaction, actor, dreamBefore, "record", ct);
            await transaction.CommitAsync(ct);
            var merged = outcome.Merged.Contains(spec.Id);
            return new RecordResult(!merged, view, superseded, merged ? true : null);
        }, ct);

    /// <summary>
    /// Stores <paramref name="specs"/> in <paramref name="project"/> within the caller's transaction:
    /// each linked to its own item, else <paramref name="itemId"/>; scoped to its repositories, else
    /// <paramref name="defaultRepository"/>, else the whole project. Refuses (and so rolls the whole
    /// write back) a repository that is not a member of the project or a node that is not in one.
    /// Answers the id of the entry a spec superseded, if any, and the lessons merged into existing ones.
    /// A lesson (S9) naming same_as, or with an exact normalized match of overlapping applies-when
    /// (and no contradicts), is merged into that lesson as a correction event instead of stored.
    /// </summary>
    private async Task<KnowledgeInsertOutcome> InsertKnowledgeAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project, IReadOnlyList<KnowledgeSpec> specs,
        Guid? itemId, Guid? featureId, string? defaultRepository, WorkActor actor, DateTime now, CancellationToken ct)
    {
        Guid? superseded = null;
        var merged = new Dictionary<Guid, Guid>();
        foreach (var spec in specs)
        {
            var linkedItem = spec.ItemId ?? itemId;
            if (linkedItem is { } item)
            {
                var itemProject = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT project_id FROM work_items WHERE id = @item", new { item }, transaction, cancellationToken: ct));
                if (itemProject != project.Id)
                    throw WorkException.Invalid($"Item {item} is not an item of {project.ProjectKey}: knowledge stays within its project. Nothing was changed.");
            }

            if (spec.Kind != KnowledgeKind.Lesson && (spec.SameAs is not null || spec.Contradicts is not null))
                throw WorkException.Invalid("same_as and contradicts are fields of lessons. Nothing was changed.");
            if (spec.Kind == KnowledgeKind.Lesson && spec.Supersedes is null &&
                await LessonMergeTargetAsync(connection, transaction, project, spec, ct) is { } target)
            {
                if (actor.Kind == WorkActor.SystemKind && actor.Name == "dreamer")
                    throw WorkException.Invalid("This lesson already exists. Read it and use a reversible relation instead of implicit capture merging.");
                await AddLessonEventAsync(connection, transaction, project, target, spec, linkedItem, actor, now, ct);
                merged[spec.Id] = target;
                continue;
            }

            var (repositories, nodes) = await ResolveScopeAsync(connection, transaction, project,
                spec.Repositories ?? (defaultRepository is null ? [] : [defaultRepository]), spec.Nodes, ct);
            var commit = spec.Commit ?? await IndexedCommitAsync(connection, transaction, repositories, nodes, ct);

            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO knowledge_entries (id, project_id, kind, status, summary, summary_hash, body, details_json, confidence, made_autonomously, " +
                "activities, source_kind, item_id, feature_id, supersedes_id, source_file, verified_commit, verified_at, revision, version, " +
                "author, author_kind, author_session_id, author_harness, author_model, author_host, created_at, updated_at) VALUES " +
                "(@id, @projectId, @kind, 'active', @summary, @summaryHash, @body, @details, @confidence, @madeAutonomously, @activities, @sourceKind, " +
                "@itemId, @featureId, @supersedes, @sourceFile, @commit, @now, 1, 1, @author, @authorKind, @session, @harness, @model, @host, @now, @now)",
                new
                {
                    id = spec.Id, projectId = project.Id, kind = JsonName(spec.Kind), summary = spec.Summary, summaryHash = KnowledgePaths.HashSummary(spec.Summary),
                    body = spec.Body, details = DetailsJson(spec.Details), confidence = JsonName(spec.Confidence), madeAutonomously = spec.MadeAutonomously,
                    activities = Activities(spec.Details), sourceKind = spec.Details?.Source is { } source ? JsonName(source.Kind) : null,
                    itemId = linkedItem, featureId = spec.FeatureId ?? featureId, supersedes = spec.Supersedes?.EntryId, sourceFile = spec.SourceFile, commit, now,
                    author = actor.Name, authorKind = actor.Kind, session = actor.SessionId, harness = actor.Harness, model = actor.Model, host = actor.Host
                },
                transaction, cancellationToken: ct));
            await WriteScopeAsync(connection, transaction, spec.Id, repositories, spec.Paths, nodes, ct);
            await SnapshotFilesAsync(connection, transaction, spec.Id, project.Id, repositories, spec.Paths, nodes, ct);
            if (spec.Kind == KnowledgeKind.Lesson)
                await InitLessonAsync(connection, transaction, project, spec, linkedItem, actor, now, ct);
            await InsertRevisionAsync(connection, transaction, spec.Id, 1, "created", actor, now, ct);

            if (spec.Supersedes is { } replaced)
            {
                var old = await AdvanceKnowledgeAsync(connection, transaction, replaced.EntryId, replaced.ExpectedVersion, now, ct);
                if (old.ProjectId != project.Id)
                    throw WorkException.Invalid($"Entry {replaced.EntryId} belongs to another project: an entry supersedes one of its own project. Nothing was changed.");
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE knowledge_entries SET status = 'superseded', superseded_by_id = @by WHERE id = @id",
                    new { by = spec.Id, id = replaced.EntryId }, transaction, cancellationToken: ct));
                // A lesson replacing a lesson (S9) takes over its corrections, recurrence, links, open
                // challenges and rule status: a rule superseded by a new wording stays a rule.
                if (old.Kind == "lesson" && spec.Kind == KnowledgeKind.Lesson)
                    await TransferLessonAsync(connection, transaction, old, spec.Id, actor, now, uniteAppliesWhen: false, ct);
                await InsertRevisionAsync(connection, transaction, replaced.EntryId, old.Revision, "superseded", actor, now, ct);
                superseded = replaced.EntryId;
            }
        }

        return new KnowledgeInsertOutcome(superseded, merged);
    }

    /// <summary>What <see cref="InsertKnowledgeAsync"/> did: the entry superseded, and spec id → lesson for the lessons merged.</summary>
    private sealed record KnowledgeInsertOutcome(Guid? Superseded, IReadOnlyDictionary<Guid, Guid> MergedInto)
    {
        public IEnumerable<Guid> Merged => MergedInto.Keys;

        public Guid EntryOf(Guid specId) => MergedInto.TryGetValue(specId, out var target) ? target : specId;
    }

    /// <summary>
    /// The save points of a repeated end_session: each must be an entry the session already stored
    /// (same kind and normalized summary), answered as its row. Anything else is refused: an ended
    /// session stores nothing more.
    /// </summary>
    private static async Task<IReadOnlyList<KnowledgeRow>> RequireRepeatedSavePointsAsync(MySqlConnection connection, MySqlTransaction transaction,
        SessionRow session, IReadOnlyList<KnowledgeSpec> knowledge, CancellationToken ct)
    {
        var stored = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns}, e.summary_hash AS summary_hash {KnowledgeFrom} WHERE e.author_session_id = @session ORDER BY e.created_at",
            new { session = session.Id }, transaction, cancellationToken: ct))).ToList();
        // A correction its end merged into an existing lesson (S9) is that lesson's event.
        var mergedIds = (await connection.QueryAsync<(Guid EntryId, string SummaryHash)>(new CommandDefinition(
            "SELECT entry_id, summary_hash FROM lesson_events WHERE session_id = @session", new { session = session.Id }, transaction, cancellationToken: ct))).ToList();
        var mergedRows = mergedIds.Count == 0
            ? []
            : (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
                $"SELECT {KnowledgeColumns}, e.summary_hash AS summary_hash {KnowledgeFrom} WHERE e.id IN @ids",
                new { ids = mergedIds.Select(merged => merged.EntryId.ToString()).Distinct().ToArray() }, transaction, cancellationToken: ct))).ToDictionary(row => row.Id);
        var rows = new List<KnowledgeRow>();
        foreach (var spec in knowledge)
        {
            var hash = KnowledgePaths.HashSummary(spec.Summary);
            var match = stored.FirstOrDefault(row => row.Kind == JsonName(spec.Kind) && row.SummaryHash == hash);
            if (match is null && spec.Kind == KnowledgeKind.Lesson &&
                mergedIds.FirstOrDefault(merged => merged.SummaryHash == hash) is { EntryId: var entryId } && mergedRows.TryGetValue(entryId, out var into))
                match = into;
            if (match is null)
                throw WorkException.Invalid(
                    $"Session {session.Id} has ended, so its save points can no longer be stored ('{spec.Summary}' is not one its end stored). " +
                    "Record it with record, or in a new session. Nothing was changed.");
            rows.Add(ToRow(match));
        }

        return rows;
    }

    public Task<KnowledgeView> ReviseKnowledgeAsync(Guid entryId, long expectedVersion, KnowledgeSpec state, WorkActor actor, CancellationToken ct = default) =>
        ChangeKnowledgeAsync(entryId, expectedVersion, "revised", actor, (connection, transaction, before, now) =>
            ApplyRevisionAsync(connection, transaction, entryId, before, state, now, ct), ct);

    // The content of a revision (record revise, and a challenge resolved amend): the entry's fields,
    // scope and file baselines replaced by <paramref name="state"/>, within the caller's transaction.
    private async Task ApplyRevisionAsync(MySqlConnection connection, MySqlTransaction transaction, Guid entryId, KnowledgeEntryRow before, KnowledgeSpec state, DateTime now, CancellationToken ct)
        {
            await RequireDreamRevisionAsync(connection, transaction, entryId, state, ct);
            var project = (await ReadProjectRowAsync(connection, transaction, before.ProjectId, ct))!;
            var (repositories, nodes) = await ResolveScopeAsync(connection, transaction, project, state.Repositories ?? [], state.Nodes, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE knowledge_entries SET summary = @summary, summary_hash = @summaryHash, body = @body, details_json = @details, confidence = @confidence, " +
                "made_autonomously = @madeAutonomously, activities = @activities, source_kind = @sourceKind, verified_commit = @commit, verified_at = @now WHERE id = @id",
                new
                {
                    summary = state.Summary, summaryHash = KnowledgePaths.HashSummary(state.Summary), body = state.Body, details = DetailsJson(state.Details),
                    confidence = JsonName(state.Confidence), madeAutonomously = state.MadeAutonomously, activities = Activities(state.Details),
                    sourceKind = state.Details?.Source is { } source ? JsonName(source.Kind) : null,
                    commit = DreamExecutionScope.Current is null ? state.Commit ?? await IndexedCommitAsync(connection, transaction, repositories, nodes, ct) : before.VerifiedCommit,
                    now = DreamExecutionScope.Current is null ? now : before.VerifiedAt, id = entryId
                },
                transaction, cancellationToken: ct));
            await WriteScopeAsync(connection, transaction, entryId, repositories, state.Paths, nodes, ct);
            if (DreamExecutionScope.Current is null)
                await SnapshotFilesAsync(connection, transaction, entryId, before.ProjectId, repositories, state.Paths, nodes, ct);
            // A lesson given an applies-when activity may now be a rule (prompt source, or recurrence).
            if (before.Kind == "lesson")
                await PromoteIfDueAsync(connection, transaction, entryId, now, promptEvent: false, ct);
        }

    public Task<KnowledgeView> RetractKnowledgeAsync(Guid entryId, long expectedVersion, string reason, WorkActor actor, CancellationToken ct = default) =>
        ChangeKnowledgeAsync(entryId, expectedVersion, "retracted", actor, (connection, transaction, _, _) =>
            connection.ExecuteAsync(new CommandDefinition(
                "UPDATE knowledge_entries SET status = 'retracted', status_reason = @reason WHERE id = @id",
                new { reason, id = entryId }, transaction, cancellationToken: ct)), ct);

    public Task<KnowledgeView> VerifyKnowledgeAsync(Guid entryId, long expectedVersion, string? commit, WorkActor actor, CancellationToken ct = default) =>
        ChangeKnowledgeAsync(entryId, expectedVersion, "verified", actor, async (connection, transaction, before, now) =>
        {
            var repositories = await ReadEntryRepositoriesAsync(connection, transaction, entryId, ct);
            var paths = await ReadEntryPathsAsync(connection, transaction, [entryId], ct);
            var nodes = await ReadEntryNodesAsync(connection, transaction, [entryId], ct);
            var nodeRefs = nodes.Select(node => new NodeRef(node.NodeId, node.RepositoryName, node.QualifiedName, node.FilePath)).ToList();
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE knowledge_entries SET verified_commit = @commit, verified_at = @now WHERE id = @id",
                new { commit = commit ?? await IndexedCommitAsync(connection, transaction, repositories, nodeRefs, ct), now, id = entryId },
                transaction, cancellationToken: ct));
            await SnapshotFilesAsync(connection, transaction, entryId, before.ProjectId, repositories,
                paths.Select(path => path.Path).ToList(), nodeRefs, ct);
            // Validate the dependencies just resolved from the CURRENT scope. A refusal rolls
            // these tentative baselines back with the whole transaction. Old baselines alone
            // would let an applies_when location change authorize unread files.
            if (DreamPolicy.IsDreamer(actor))
            {
                DreamPolicy.RequireVerification(actor, (await LoadKnowledgeViewAsync(connection, transaction, entryId, false, ct))!);
                // Locking reads see the current committed index, even if earlier reads in this
                // transaction established an older repeatable-read snapshot. Hold the index rows
                // through commit so a successful proof cannot race a newly indexed revision.
                var files = await connection.QueryAsync<(string RepositoryName, string Path, string? BaselineHash, string? IndexedHash)>(new CommandDefinition(
                    "SELECT k.repository_name,k.path,k.content_hash AS baseline_hash,h.content_hash AS indexed_hash FROM knowledge_entry_files k LEFT JOIN file_hashes h ON h.repository=k.repository_name AND h.rel_path=k.path WHERE k.entry_id=@entryId LOCK IN SHARE MODE",
                    new { entryId }, transaction, cancellationToken: ct));
                foreach (var file in files)
                {
                    var readHash = DreamExecutionScope.Current!.ReadFileHash(file.RepositoryName, file.Path);
                    if (readHash is null || !string.Equals(readHash, file.IndexedHash, StringComparison.OrdinalIgnoreCase) || !string.Equals(readHash, file.BaselineHash, StringComparison.OrdinalIgnoreCase))
                        throw new WorkException(WorkErrorCodes.Forbidden, "The source read no longer matches the indexed file being verified. Read the current source again; nothing was changed.");
                }
            }
        }, ct);

    // A change of an existing entry: the versioned advance, the entry must be active, the change, a
    // revision with the entry as it is after, all in one transaction.
    private Task<KnowledgeView> ChangeKnowledgeCoreAsync(Guid entryId, long expectedVersion, string action, WorkActor actor,
        Func<MySqlConnection, MySqlTransaction, KnowledgeEntryRow, DateTime, Task> change, CancellationToken ct) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("SELECT id FROM knowledge_entries WHERE id=@entryId FOR UPDATE", new { entryId }, transaction, cancellationToken: ct));
            var dreamBefore = await CaptureDreamBeforeAsync(connection, transaction, actor, [entryId], ct);
            var before = await AdvanceKnowledgeAsync(connection, transaction, entryId, expectedVersion, now, ct);
            await change(connection, transaction, before, now);
            await InsertRevisionAsync(connection, transaction, entryId, before.Revision, action, actor, now, ct);
            await JournalDreamAfterAsync(connection, transaction, actor, dreamBefore, action, ct);
            var view = (await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct))!;
            await transaction.CommitAsync(ct);
            return view;
        }, ct);

    /// <summary>
    /// The conditional versioned UPDATE of an entry (version and revision advance, the row is held):
    /// refused not_found, conflict (another version, with the current entry), or conflict when the
    /// entry is no longer active (only an active entry changes). Answers the entry as it was.
    /// </summary>
    private async Task<KnowledgeEntryRow> AdvanceKnowledgeAsync(MySqlConnection connection, MySqlTransaction transaction, Guid entryId, long expectedVersion, DateTime now, CancellationToken ct)
    {
        var advanced = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entries SET version = version + 1, revision = revision + 1, updated_at = @now WHERE id = @id AND version = @expectedVersion",
            new { now, id = entryId, expectedVersion }, transaction, cancellationToken: ct));
        if (advanced == 0)
        {
            var current = await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct)
                ?? throw WorkException.NotFound($"There is no knowledge entry {entryId}.");
            throw new WorkException(WorkErrorCodes.Conflict,
                $"Entry {entryId} is at version {current.Version}, not {expectedVersion}: it was changed after it was read. Nothing was changed; read it again (in current) and repeat the call.",
                current);
        }

        var row = (await ReadKnowledgeRowAsync(connection, transaction, entryId, ct))!;
        if (row.Status != "active")
        {
            var current = (await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct))! with { Version = row.Version - 1, Revision = row.Revision - 1 };
            throw new WorkException(WorkErrorCodes.Conflict,
                $"Entry {entryId} is {row.Status}: only an active entry is revised, verified, retracted or superseded. Record a new entry instead. Nothing was changed.",
                current);
        }

        // Its version and revision are already the ones this write gives it.
        return row;
    }

    private static async Task InsertRevisionAsync(MySqlConnection connection, MySqlTransaction transaction, Guid entryId, int revision, string action, WorkActor actor, DateTime now, CancellationToken ct)
    {
        var snapshot = await LoadKnowledgeViewAsync(connection, transaction, entryId, withRevisions: false, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO knowledge_revisions (entry_id, revision, action, snapshot_json, author, author_kind, author_session_id, author_harness, author_model, author_host, created_at) " +
            "VALUES (@entryId, @revision, @action, @snapshot, @author, @authorKind, @session, @harness, @model, @host, @now)",
            new
            {
                entryId, revision, action, snapshot = JsonSerializer.Serialize(snapshot, WorkJson.Options),
                author = actor.Name, authorKind = actor.Kind, session = actor.SessionId, harness = actor.Harness, model = actor.Model, host = actor.Host, now
            },
            transaction, cancellationToken: ct));
    }

    // ---- Scope and freshness -------------------------------------------------------------------

    // The repositories (in the project's spelling) and nodes of an entry, each a member of the project.
    private static async Task<(IReadOnlyList<string> Repositories, IReadOnlyList<NodeRef> Nodes)> ResolveScopeAsync(
        MySqlConnection connection, MySqlTransaction? transaction, ProjectRow project, IReadOnlyList<string> repositories, IReadOnlyList<long> nodeIds, CancellationToken ct)
    {
        var members = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT repository_name FROM work_project_repositories WHERE project_id = @projectId ORDER BY position, repository_name",
            new { projectId = project.Id }, transaction, cancellationToken: ct))).ToList();
        var resolved = new List<string>();
        foreach (var repository in repositories)
        {
            var member = members.FirstOrDefault(name => string.Equals(name, repository, StringComparison.OrdinalIgnoreCase))
                ?? throw WorkException.Invalid(
                    $"'{repository}' is not a repository of {project.ProjectKey} ({(members.Count == 0 ? "it has none" : string.Join(", ", members))}): knowledge stays within its project's repositories. Nothing was changed.");
            if (!resolved.Contains(member, StringComparer.Ordinal))
                resolved.Add(member);
        }

        var nodes = new List<NodeRef>();
        if (nodeIds.Count > 0)
        {
            var ids = nodeIds.Distinct().ToArray();
            var found = (await connection.QueryAsync<(long Id, string Project, string QualifiedName, string? FilePath)>(new CommandDefinition(
                "SELECT id, repository, qualified_name, file_path FROM nodes WHERE id IN @ids", new { ids }, transaction, cancellationToken: ct)))
                .ToDictionary(node => node.Id);
            foreach (var id in ids)
            {
                if (!found.TryGetValue(id, out var node))
                    throw WorkException.Invalid($"There is no graph node {id}. Nothing was changed.");
                var member = members.FirstOrDefault(name => string.Equals(name, node.Project, StringComparison.OrdinalIgnoreCase))
                    ?? throw WorkException.Invalid($"Graph node {id} is in repository '{node.Project}', which is not a repository of {project.ProjectKey}. Nothing was changed.");
                nodes.Add(new NodeRef(id, member, node.QualifiedName, string.IsNullOrEmpty(node.FilePath) ? null : node.FilePath));
            }
        }

        return (resolved, nodes);
    }

    private static async Task WriteScopeAsync(MySqlConnection connection, MySqlTransaction transaction, Guid entryId, IReadOnlyList<string> repositories,
        IReadOnlyList<string> paths, IReadOnlyList<NodeRef> nodes, CancellationToken ct)
    {
        foreach (var table in new[] { "knowledge_entry_repositories", "knowledge_entry_paths", "knowledge_entry_nodes" })
            await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM {table} WHERE entry_id = @entryId", new { entryId }, transaction, cancellationToken: ct));

        if (repositories.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO knowledge_entry_repositories (entry_id, repository_name) VALUES (@entryId, @repository)",
                repositories.Select(repository => new { entryId, repository }), transaction, cancellationToken: ct));
        if (paths.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO knowledge_entry_paths (entry_id, path, prefix) VALUES (@entryId, @path, @prefix)",
                paths.Distinct(StringComparer.Ordinal).Select(path => new { entryId, path, prefix = KnowledgePaths.Prefix(path) }), transaction, cancellationToken: ct));
        if (nodes.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO knowledge_entry_nodes (entry_id, node_id, repository_name, qualified_name, file_path) VALUES (@entryId, @Id, @Repository, @QualifiedName, @FilePath)",
                nodes.Select(node => new { entryId, node.Id, node.Repository, node.QualifiedName, node.FilePath }), transaction, cancellationToken: ct));
    }

    /// <summary>
    /// The files the entry is verified against, with their indexed content hash now: its concrete
    /// paths (not globs) in its repositories (the project's when it names none) and its nodes' files.
    /// A path not indexed yet in a repository the entry names (or a node's file) is kept with a NULL
    /// hash and baselined after commit by the index write or this write, whichever commits later (<see cref="KnowledgeBaselines"/>).
    /// A found file is stored in the spelling file_hashes uses, so the freshness join is exact; paths
    /// are looked up in both separator spellings.
    /// </summary>
    private static async Task SnapshotFilesAsync(MySqlConnection connection, MySqlTransaction transaction, Guid entryId, Guid projectId,
        IReadOnlyList<string> repositories, IReadOnlyList<string> paths, IReadOnlyList<NodeRef> nodes, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM knowledge_entry_files WHERE entry_id = @entryId", new { entryId }, transaction, cancellationToken: ct));

        var files = new Dictionary<(string Repository, string Path), string?>();
        var concrete = paths.Where(path => !KnowledgePaths.IsGlob(path)).Distinct(StringComparer.Ordinal).ToArray();
        var targets = repositories.Count > 0
            ? repositories.ToArray()
            : (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT repository_name FROM work_project_repositories WHERE project_id = @projectId", new { projectId }, transaction, cancellationToken: ct))).ToArray();

        var lookups = concrete.SelectMany(path => targets.Select(repository => (repository, path)))
            .Concat(nodes.Where(node => node.FilePath is not null).Select(node => (repository: node.Repository, path: node.FilePath!)))
            .Distinct()
            .ToList();
        if (lookups.Count > 0)
        {
            var variants = lookups.SelectMany(lookup => new[] { lookup.path.Replace('\\', '/'), lookup.path.Replace('/', '\\') }).Distinct(StringComparer.Ordinal).ToArray();
            var found = (await connection.QueryAsync<(string Project, string RelPath, string ContentHash)>(new CommandDefinition(
                "SELECT repository, rel_path, content_hash FROM file_hashes WHERE repository IN @repositories AND rel_path IN @variants",
                new { repositories = lookups.Select(lookup => lookup.repository).Distinct().ToArray(), variants },
                transaction, cancellationToken: ct))).ToList();
            foreach (var (repository, path) in lookups)
            {
                var forward = path.Replace('\\', '/');
                var row = found.FirstOrDefault(candidate =>
                    string.Equals(candidate.Project, repository, StringComparison.OrdinalIgnoreCase) && candidate.RelPath.Replace('\\', '/') == forward);
                var named = repositories.Contains(repository, StringComparer.Ordinal) || nodes.Any(node => node.Repository == repository && node.FilePath == path);
                if (row.ContentHash is not null)
                    files[(repository, row.RelPath)] = row.ContentHash;
                else if (named)
                    files[(repository, forward)] = null;
            }
        }

        if (files.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO knowledge_entry_files (entry_id, repository_name, path, content_hash) VALUES (@entryId, @repository, @path, @hash)",
                files.Select(file => new { entryId, repository = file.Key.Repository, path = file.Key.Path, hash = file.Value }), transaction, cancellationToken: ct));
    }

    // The indexed commit of the one repository an entry is scoped to (through its repositories or nodes).
    private static async Task<string?> IndexedCommitAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<string> repositories, IReadOnlyList<NodeRef> nodes, CancellationToken ct)
    {
        var scoped = repositories.Concat(nodes.Select(node => node.Repository)).Distinct(StringComparer.Ordinal).ToList();
        return scoped.Count != 1
            ? null
            : await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT last_commit_sha FROM repositories WHERE name = @name", new { name = scoped[0] }, transaction, cancellationToken: ct));
    }

    // ---- Reads -------------------------------------------------------------------------------

    private static Task<KnowledgeEntryRow?> ReadKnowledgeRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid entryId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.id = @entryId", new { entryId }, transaction, cancellationToken: ct));

    private static async Task<IReadOnlyList<string>> ReadEntryRepositoriesAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid entryId, CancellationToken ct) =>
        (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT repository_name FROM knowledge_entry_repositories WHERE entry_id = @entryId ORDER BY repository_name",
            new { entryId }, transaction, cancellationToken: ct))).ToList();

    private static async Task<IReadOnlyList<(Guid EntryId, string Path)>> ReadEntryPathsAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<Guid> entryIds, CancellationToken ct) =>
        entryIds.Count == 0
            ? []
            : (await connection.QueryAsync<(Guid EntryId, string Path)>(new CommandDefinition(
                "SELECT entry_id, path FROM knowledge_entry_paths WHERE entry_id IN @entryIds ORDER BY path",
                new { entryIds = entryIds.ToArray() }, transaction, cancellationToken: ct))).ToList();

    private sealed class EntryNodeRow
    {
        public Guid EntryId { get; init; }
        public long NodeId { get; init; }
        public string RepositoryName { get; init; } = "";
        public string QualifiedName { get; init; } = "";
        public string? FilePath { get; init; }
    }

    private static async Task<IReadOnlyList<EntryNodeRow>> ReadEntryNodesAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<Guid> entryIds, CancellationToken ct) =>
        entryIds.Count == 0
            ? []
            : (await connection.QueryAsync<EntryNodeRow>(new CommandDefinition(
                "SELECT entry_id, node_id, repository_name, qualified_name, file_path FROM knowledge_entry_nodes WHERE entry_id IN @entryIds ORDER BY node_id",
                new { entryIds = entryIds.ToArray() }, transaction, cancellationToken: ct))).ToList();

    private static async Task<KnowledgeView?> LoadKnowledgeViewAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid entryId, bool withRevisions, CancellationToken ct)
    {
        var row = await ReadKnowledgeRowAsync(connection, transaction, entryId, ct);
        if (row is null)
            return null;

        var repositories = await ReadEntryRepositoriesAsync(connection, transaction, entryId, ct);
        var paths = (await ReadEntryPathsAsync(connection, transaction, [entryId], ct)).Select(path => path.Path).ToList();
        var nodes = (await ReadEntryNodesAsync(connection, transaction, [entryId], ct))
            .Select(node => new KnowledgeNodeView(node.NodeId, node.RepositoryName, node.QualifiedName, node.FilePath)).ToList();
        var files = (await connection.QueryAsync<(string RepositoryName, string Path, string? ContentHash, string? CurrentHash)>(new CommandDefinition(
            "SELECT kf.repository_name, kf.path, kf.content_hash, h.content_hash FROM knowledge_entry_files kf " +
            "LEFT JOIN file_hashes h ON h.repository = kf.repository_name AND h.rel_path = kf.path WHERE kf.entry_id = @entryId ORDER BY kf.repository_name, kf.path",
            new { entryId }, transaction, cancellationToken: ct))).ToList();
        var stale = files.Where(file => file.ContentHash is not null && file.CurrentHash != file.ContentHash)
            .Select(file => new KnowledgeFileView(file.RepositoryName, file.Path)).ToList();

        IReadOnlyList<KnowledgeRevisionView>? revisions = null;
        if (withRevisions)
        {
            revisions = (await connection.QueryAsync<(int Revision, string Action, string Author, string AuthorKind, string? AuthorSessionId, DateTime CreatedAt)>(new CommandDefinition(
                    "SELECT revision, action, author, author_kind, author_session_id, created_at FROM knowledge_revisions WHERE entry_id = @entryId ORDER BY revision DESC",
                    new { entryId }, transaction, cancellationToken: ct)))
                .Select(revision => new KnowledgeRevisionView(revision.Revision, revision.Action,
                    new WorkActor(revision.Author, revision.AuthorKind, revision.AuthorSessionId), Utc(revision.CreatedAt)))
                .ToList();
        }

        return new KnowledgeView(
            row.Id, row.ProjectId, row.ProjectKey, ParseName<KnowledgeKind>(row.Kind), ParseName<KnowledgeStatus>(row.Status), row.Summary,
            ParseName<KnowledgeConfidence>(row.Confidence), row.PossiblyStale, row.Revision, row.Version,
            new WorkActor(row.Author, row.AuthorKind, row.AuthorSessionId, row.AuthorHarness, row.AuthorModel, row.AuthorHost),
            Utc(row.VerifiedAt), Utc(row.CreatedAt), Utc(row.UpdatedAt),
            Body: row.Body,
            Details: row.DetailsJson is { } json ? JsonSerializer.Deserialize<KnowledgeDetails>(json, WorkJson.Options) : null,
            MadeAutonomously: row.MadeAutonomously,
            ReviewedAt: row.ReviewedAt is { } reviewedAt ? Utc(reviewedAt) : null,
            Repositories: repositories.Count == 0 ? null : repositories,
            Paths: paths.Count == 0 ? null : paths,
            Nodes: nodes.Count == 0 ? null : nodes,
            Item: row.ItemId is { } itemId ? new EntityRef(itemId, row.ItemKey!, row.ItemTitle ?? "") : null,
            Feature: row.FeatureId is { } featureId && row.FeatureNumber is { } featureNumber
                ? new EntityRef(featureId, WorkKeys.Feature(row.ProjectKey, featureNumber), row.FeatureTitle ?? "") : null,
            Supersedes: row.SupersedesId,
            SupersededBy: row.SupersededById,
            StatusReason: row.StatusReason,
            VerifiedCommit: row.VerifiedCommit,
            VerifiedFiles: files.Count == 0 ? null : files.Select(file => new KnowledgeFileView(file.RepositoryName, file.Path)).ToList(),
            StaleFiles: stale.Count == 0 ? null : stale,
            SourceFile: row.SourceFile,
            Revisions: revisions,
            Lesson: row.Kind == "lesson" ? await LoadLessonViewAsync(connection, transaction, row, withEvents: withRevisions, ct) : null);
    }

    private static WorkActor AuthorOf(KnowledgeEntryRow row) =>
        new(row.Author, row.AuthorKind, row.AuthorSessionId, row.AuthorHarness, row.AuthorModel, row.AuthorHost);

    private static KnowledgeRow ToRow(KnowledgeEntryRow row, string? match = null)
    {
        var status = ParseName<KnowledgeStatus>(row.Status);
        return new KnowledgeRow(
            row.Id, ParseName<KnowledgeKind>(row.Kind), row.Summary, ParseName<KnowledgeConfidence>(row.Confidence), row.Version,
            Status: status == KnowledgeStatus.Active ? null : status,
            PossiblyStale: row.PossiblyStale ? true : null,
            MadeAutonomously: row.MadeAutonomously == true ? true : null,
            Item: row.ItemKey,
            Match: match,
            Rule: row.IsRule ? true : null,
            Recurrence: row.Recurrence is > 1 ? row.Recurrence : null);
    }

    public async Task<KnowledgeView?> GetKnowledgeAsync(Guid entryId, bool withRevisions, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var view=await LoadKnowledgeViewAsync(connection, null, entryId, withRevisions, ct);
        if(view is not null) DreamPolicy.RequireCurrentProject(view.ProjectId);
        return view;
    }

    public async Task<IReadOnlyList<KnowledgeRow>> GetKnowledgeRowsAsync(IReadOnlyList<Guid> entryIds, CancellationToken ct = default)
    {
        if (entryIds.Count == 0)
            return [];
        await using var connection = await OpenAsync(ct);
        // A lesson a write merged into an existing one (S9) answers as that lesson, marked merged.
        var ids = entryIds.Distinct().ToArray();
        var mergedInto = (await connection.QueryAsync<(Guid CorrectionId, Guid EntryId)>(new CommandDefinition(
            "SELECT correction_id, entry_id FROM lesson_events WHERE correction_id IN @ids AND correction_id <> entry_id",
            new { ids }, cancellationToken: ct))).ToDictionary(merged => merged.CorrectionId, merged => merged.EntryId);
        var rows = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.id IN @ids", new { ids = ids.Concat(mergedInto.Values).Distinct().ToArray() }, cancellationToken: ct)))
            .ToDictionary(row => row.Id);
        foreach(var row in rows.Values) DreamPolicy.RequireCurrentProject(row.ProjectId);
        var answered = new List<KnowledgeRow>();
        foreach (var id in entryIds)
        {
            if (mergedInto.TryGetValue(id, out var target) && rows.TryGetValue(target, out var into))
                answered.Add(ToRow(into) with { Merged = true });
            else if (rows.TryGetValue(id, out var row))
                answered.Add(ToRow(row));
        }

        return answered;
    }

    public async Task<RecallPage> RecallAsync(RecallSpec spec, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(spec.ProjectId);
        // Reads write nothing: baselines are adopted at index time (KnowledgeBaselines).
        await using var connection = await OpenAsync(ct);

        var where = new StringBuilder("e.project_id = @projectId AND e.status IN @statuses");
        var parameters = new DynamicParameters();
        parameters.Add("projectId", spec.ProjectId);
        parameters.Add("statuses", spec.Statuses.Select(JsonName).ToArray());
        if (spec.Kinds is { Count: > 0 } kinds)
        {
            where.Append(" AND e.kind IN @kinds");
            parameters.Add("kinds", kinds.Select(JsonName).ToArray());
        }

        if (spec.Repository is { } repository)
        {
            where.Append(" AND EXISTS (SELECT 1 FROM knowledge_entry_repositories r WHERE r.entry_id = e.id AND r.repository_name = @repository)");
            parameters.Add("repository", repository);
        }

        if (spec.ItemId is { } itemId)
        {
            where.Append(" AND e.item_id = @itemId");
            parameters.Add("itemId", itemId);
        }

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

        if (spec.RuleStates is { Count: > 0 } ruleStates)
        {
            where.Append(" AND e.rule_state IN @ruleStates");
            parameters.Add("ruleStates", ruleStates.Select(JsonName).ToArray());
        }

        for (var index = 0; index < spec.Words.Count; index++)
        {
            where.Append($" AND (e.summary LIKE @w{index} OR e.body LIKE @w{index})");
            parameters.Add($"w{index}", "%" + EscapeLike(spec.Words[index]) + "%");
        }

        if (spec.PossiblyStale is { } stale)
            where.Append(stale ? $" AND {StaleCondition}" : $" AND NOT {StaleCondition}");

        // The query's paths, and the files and identities of its nodes.
        var queryPaths = spec.Paths.ToList();
        var queryNodes = new HashSet<(string Repository, string QualifiedName)>();
        if (spec.Nodes.Count > 0)
        {
            var nodes = (await connection.QueryAsync<(long Id, string Project, string QualifiedName, string? FilePath)>(new CommandDefinition(
                "SELECT id, repository, qualified_name, file_path FROM nodes WHERE id IN @ids", new { ids = spec.Nodes.Distinct().ToArray() }, cancellationToken: ct))).ToList();
            var missing = spec.Nodes.Where(id => nodes.All(node => node.Id != id)).ToList();
            if (missing.Count > 0)
                throw WorkException.Invalid($"There is no graph node {string.Join(", ", missing)}.");
            foreach (var node in nodes)
            {
                queryNodes.Add((node.Project, node.QualifiedName));
                if (!string.IsNullOrEmpty(node.FilePath))
                    queryPaths.Add(node.FilePath);
            }
        }

        var scoped = queryPaths.Count > 0 || queryNodes.Count > 0;
        var collected = new List<KnowledgeEntryRow>();
        var after = spec.After;
        var scanned = 0;
        KnowledgeCursor? lastScanned = null;
        var exhausted = false;
        while (collected.Count <= spec.Limit && scanned < MaxRecallScan)
        {
            var page = new StringBuilder(where.ToString());
            var batchParameters = new DynamicParameters(parameters);
            if (after is { } cursor)
            {
                page.Append(" AND (e.created_at < @afterCreated OR (e.created_at = @afterCreated AND e.id < @afterId))");
                batchParameters.Add("afterCreated", cursor.CreatedAt);
                batchParameters.Add("afterId", cursor.Id.ToString());
            }

            var batch = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
                $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE {page} ORDER BY e.created_at DESC, e.id DESC LIMIT {RecallBatch}",
                batchParameters, cancellationToken: ct))).ToList();
            var fetched = batch.Count;
            scanned += fetched;
            if (batch.Count > 0)
            {
                lastScanned = new KnowledgeCursor(batch[^1].CreatedAt, batch[^1].Id);
                after = lastScanned;
            }

            if (scoped && batch.Count > 0)
            {
                var ids = batch.Select(row => row.Id).ToList();
                var paths = (await ReadEntryPathsAsync(connection, null, ids, ct)).ToLookup(path => path.EntryId, path => path.Path);
                var nodes = (await ReadEntryNodesAsync(connection, null, ids, ct)).ToLookup(node => node.EntryId);
                batch = batch.Where(row =>
                        nodes[row.Id].Any(node => queryNodes.Contains((node.RepositoryName, node.QualifiedName))) ||
                        paths[row.Id].Any(path => queryPaths.Any(query => KnowledgePaths.Covers(path, query))))
                    .ToList();
            }

            collected.AddRange(batch);
            if (fetched < RecallBatch)
            {
                exhausted = true;
                break;
            }
        }

        var hasMore = collected.Count > spec.Limit || (!exhausted && lastScanned is not null);
        var answered = collected.Take(spec.Limit).ToList();
        KnowledgeCursor? next = null;
        if (hasMore)
            next = collected.Count > spec.Limit
                ? new KnowledgeCursor(answered[^1].CreatedAt, answered[^1].Id)
                : lastScanned;

        IReadOnlyList<object> items;
        if (spec.Detail == WorkDetail.Full)
        {
            var views = new List<object>();
            foreach (var row in answered)
                views.Add((await LoadKnowledgeViewAsync(connection, null, row.Id, withRevisions: false, ct))!);
            items = views;
        }
        else
        {
            items = answered.Select(row => (object)ToRow(row)).ToList();
        }

        return new RecallPage(items, next);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<IReadOnlyList<KnowledgeRow>> ReadRelevantKnowledgeAsync(KnowledgeContext context, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(context.ProjectId);
        // Reads write nothing: baselines are adopted at index time (KnowledgeBaselines). Each tier is
        // its own query, filtered in SQL, so a matching entry is never crowded out by newer unrelated ones.
        await using var connection = await OpenAsync(ct);
        var parameters = new DynamicParameters();
        parameters.Add("projectId", context.ProjectId);
        parameters.Add("kinds", context.Kinds.Select(JsonName).ToArray());
        parameters.Add("items", context.ItemIds.Select(id => id.ToString()).ToArray());
        var repository = string.IsNullOrWhiteSpace(context.Repository) ? null : context.Repository.Trim();
        parameters.Add("repository", repository ?? "");
        const string active = "e.project_id = @projectId AND e.status = 'active' AND e.kind IN @kinds";
        const string hasPaths = "EXISTS (SELECT 1 FROM knowledge_entry_paths kp WHERE kp.entry_id = e.id)";
        const string unscoped = "NOT EXISTS (SELECT 1 FROM knowledge_entry_repositories r WHERE r.entry_id = e.id)";
        const string inRepository = "EXISTS (SELECT 1 FROM knowledge_entry_repositories r WHERE r.entry_id = e.id AND r.repository_name = @repository)";
        var notItem = context.ItemIds.Count == 0 ? "" : " AND (e.item_id IS NULL OR e.item_id NOT IN @items)";
        // Lessons beyond the item (S9): a task-scoped lesson lives on its item. A briefing serves
        // rules in its own section (by applies-when), so its other tiers take no lesson; an item brief
        // takes a rule that is not addressed locally.
        notItem += context.RulesServedApart
            ? " AND e.kind <> 'lesson'"
            : " AND (e.kind <> 'lesson' OR (e.rule_state = 'rule' AND NOT EXISTS (SELECT 1 FROM lesson_addressed_by ab WHERE ab.entry_id = e.id)))";

        var ranked = new List<(KnowledgeEntryRow Row, int Tier, string Match)>();
        if (context.ItemIds.Count > 0)
        {
            foreach (var row in await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
                $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE {active} AND e.item_id IN @items ORDER BY e.updated_at DESC, e.id LIMIT {context.Limit}",
                parameters, cancellationToken: ct)))
                ranked.Add((row, 0, "item"));
        }

        if (context.Paths.Count > 0)
        {
            // Prefilter on each path's literal prefix (a superset of what Covers accepts), then match.
            var clauses = new List<string>();
            for (var index = 0; index < context.Paths.Count; index++)
            {
                clauses.Add($"@q{index} LIKE CONCAT(kp.prefix, '%') OR kp.prefix LIKE @qp{index}");
                parameters.Add($"q{index}", context.Paths[index]);
                parameters.Add($"qp{index}", EscapeLike(KnowledgePaths.Prefix(context.Paths[index])) + "%");
            }

            // A session without a repository matches path-scoped entries in any repository of the project.
            var scope = repository is null ? "" : $" AND ({unscoped} OR {inRepository})";
            var rows = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
                $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE {active}{notItem}{scope} AND EXISTS (SELECT 1 FROM knowledge_entry_paths kp " +
                $"WHERE kp.entry_id = e.id AND ({string.Join(" OR ", clauses)})) ORDER BY e.updated_at DESC, e.id LIMIT {MaxRelevantCandidates}",
                parameters, cancellationToken: ct))).ToList();
            var paths = (await ReadEntryPathsAsync(connection, null, rows.Select(row => row.Id).ToList(), ct)).ToLookup(path => path.EntryId, path => path.Path);
            foreach (var row in rows.Where(row => paths[row.Id].Any(path => context.Paths.Any(query => KnowledgePaths.Covers(path, query)))).Take(context.Limit))
                ranked.Add((row, 1, "path"));
        }

        // Entries without paths: scoped to the repository, or project-wide. An attempt belongs to the
        // work it was tried on, so only by item or path. A path-scoped entry is relevant only to its paths.
        var wide = repository is null ? unscoped : $"({unscoped} OR {inRepository})";
        foreach (var row in await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns}, {unscoped} AS project_wide {KnowledgeFrom} WHERE {active}{notItem} AND e.kind <> 'attempt' AND NOT {hasPaths} AND {wide} " +
            $"ORDER BY project_wide, e.updated_at DESC, e.id LIMIT {context.Limit}",
            parameters, cancellationToken: ct)))
            ranked.Add((row, row.ProjectWide != 0 ? 3 : 2, row.ProjectWide != 0 ? "project" : "repository"));

        return ranked
            .OrderBy(entry => entry.Tier)
            // A lesson from the human's prompt first (highest priority), then the newest.
            .ThenByDescending(entry => entry.Row.Priority == "highest")
            .ThenByDescending(entry => entry.Row.UpdatedAt)
            .Take(context.Limit)
            .Select(entry => ToRow(entry.Row, entry.Match))
            .ToList();
    }

    // ---- Import (§10.3) --------------------------------------------------------------------------

    private Task<ImportKnowledgeResult> ImportKnowledgeCoreAsync(Guid projectId, IReadOnlyList<ImportCandidateSpec> candidates, string previewToken,
        IReadOnlyList<int>? accept, string? keyHash, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            // A dry run reads in a transaction it rolls back: it stores nothing.
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var project = accept is null
                ? await ReadProjectRowAsync(connection, transaction, projectId, ct) ?? throw WorkException.NotFound($"There is no project {projectId}.")
                : await HoldProjectAsync(connection, transaction, projectId, ct);

            // A repeated confirm (held project row: the first one has committed) answers the first result.
            if (accept is not null && keyHash is not null &&
                await FindKeyAsync(connection, transaction, projectId, ImportOperation, keyHash, ct) is { ResultJson: { } first })
            {
                await transaction.CommitAsync(ct);
                return JsonSerializer.Deserialize<ImportKnowledgeResult>(first, WorkJson.Options)!;
            }

            var outcomes = new List<ImportCandidateResult>();
            var seen = new Dictionary<(KnowledgeKind Kind, string Hash), int>();
            foreach (var candidate in candidates)
            {
                outcomes.Add(await PreviewCandidateAsync(connection, transaction, project, candidate, seen, ct));
            }

            if (accept is null)
            {
                await transaction.RollbackAsync(ct);
                return new ImportKnowledgeResult(false, previewToken, outcomes);
            }

            var refused = accept.Distinct()
                .Select(index => (index, outcome: outcomes.FirstOrDefault(outcome => outcome.Index == index)))
                .Where(entry => entry.outcome is not { Outcome: ImportOutcomes.WouldStore })
                .ToList();
            if (refused.Count > 0)
                throw WorkException.Invalid(
                    "Only candidates that preview as would_store can be accepted; " +
                    string.Join("; ", refused.Select(entry => entry.outcome is null ? $"there is no candidate {entry.index}" : $"candidate {entry.index} is {entry.outcome.Outcome}")) +
                    ". Nothing was stored.");

            var accepted = accept.Distinct().ToHashSet();
            var specs = candidates.Where(candidate => accepted.Contains(candidate.Index)).Select(candidate => candidate.Spec!).ToList();
            await InsertKnowledgeAsync(connection, transaction, project, specs, null, null, null, actor, now, ct);
            var stored = new Dictionary<Guid, KnowledgeRow>();
            foreach (var spec in specs)
                stored[spec.Id] = ToRow((await ReadKnowledgeRowAsync(connection, transaction, spec.Id, ct))!);

            var result = new ImportKnowledgeResult(true, previewToken, outcomes.Select(outcome =>
                accepted.Contains(outcome.Index)
                    ? outcome with { Outcome = ImportOutcomes.Stored, Entry = stored[candidates.First(candidate => candidate.Index == outcome.Index).Spec!.Id] }
                    : outcome).ToList());
            if (keyHash is not null)
                await InsertKeyAsync(connection, transaction, projectId, ImportOperation, keyHash, null, JsonSerializer.Serialize(result, WorkJson.Options), now, ct);
            await transaction.CommitAsync(ct);
            return result;
        }, ct);

    private async Task<ImportCandidateResult> PreviewCandidateAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectRow project,
        ImportCandidateSpec candidate, Dictionary<(KnowledgeKind Kind, string Hash), int> seen, CancellationToken ct)
    {
        if (candidate.Spec is not { } spec)
            return new ImportCandidateResult(candidate.Index, ImportOutcomes.Invalid, candidate.InvalidMessage);
        if (candidate.Verification == ImportVerification.Personal)
            return new ImportCandidateResult(candidate.Index, ImportOutcomes.Personal, "A personal preference belongs in harness memory, not in the project's shared knowledge.");
        if (candidate.Verification == ImportVerification.Stale)
            return new ImportCandidateResult(candidate.Index, ImportOutcomes.Stale, "The candidate no longer matches the repository; correct it and mark it verified, or drop it.");

        try
        {
            await ResolveScopeAsync(connection, transaction, project, spec.Repositories ?? [], spec.Nodes, ct);
        }
        catch (WorkException exception) when (exception.Code == WorkErrorCodes.InvalidArgument)
        {
            return new ImportCandidateResult(candidate.Index, ImportOutcomes.OutOfScope, exception.Message.Replace(" Nothing was changed.", "", StringComparison.Ordinal));
        }

        var hash = KnowledgePaths.HashSummary(spec.Summary);
        var existing = await connection.QueryFirstOrDefaultAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.project_id = @projectId AND e.kind = @kind AND e.summary_hash = @hash AND e.status = 'active' ORDER BY e.created_at LIMIT 1",
            new { projectId = project.Id, kind = JsonName(spec.Kind), hash }, transaction, cancellationToken: ct));
        if (existing is not null)
            return new ImportCandidateResult(candidate.Index, ImportOutcomes.Duplicate, "An active entry of the project already says this.", DuplicateOf: ToRow(existing));
        if (seen.TryGetValue((spec.Kind, hash), out var earlier))
            return new ImportCandidateResult(candidate.Index, ImportOutcomes.Duplicate, $"Candidate {earlier} of this batch says the same.", DuplicateOfCandidate: earlier);

        seen[(spec.Kind, hash)] = candidate.Index;
        return new ImportCandidateResult(candidate.Index, ImportOutcomes.WouldStore);
    }

    // ---- JSON of the details -----------------------------------------------------------------------

    private static string? DetailsJson(KnowledgeDetails? details) =>
        details is null || details == new KnowledgeDetails() ? null : JsonSerializer.Serialize(details, WorkJson.Options);

    private static string? Activities(KnowledgeDetails? details) =>
        details?.AppliesWhen?.Activity is { Count: > 0 } activities ? string.Join(',', activities.Select(JsonName)) : null;
}
