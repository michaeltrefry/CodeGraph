using System.Data;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore : IDreamProposalStore
{
    public const string DreamProposalLockName = "codegraph_dream_proposals";
    private sealed class ProposalRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public Guid RunId { get; init; }
        public string Action { get; init; } = "";
        public Guid TargetId { get; init; }
        public long TargetVersion { get; init; }
        public Guid? OtherId { get; init; }
        public long? OtherVersion { get; init; }
        public string Rationale { get; init; } = "";
        public string Prevalence { get; init; } = "";
        public string EvidenceJson { get; init; } = "[]";
        public string? ReplacementJson { get; init; }
        public string? ReplacementInputJson { get; init; }
        public string? Draft { get; init; }
        public string Status { get; init; } = "";
        public long Version { get; init; }
        public string AuthorJson { get; init; } = "{}";
        public DateTime CreatedAt { get; init; }
        public string? ReviewReason { get; init; }
        public string? ReviewedByJson { get; init; }
        public DateTime? ReviewedAt { get; init; }
        public long? RejectionGraphHead { get; init; }
    }

    private static bool IsDreamer(WorkActor actor) => actor.Kind == WorkActor.SystemKind && actor.Name == "dreamer";
    // Persist constructor nulls as well: the public wire options omit them, but KnowledgeSpec's
    // nullable constructor fields are required when the stored draft is read back for acceptance.
    private static readonly JsonSerializerOptions ProposalStorageOptions = new(WorkJson.Options)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        RespectRequiredConstructorParameters = false
    };
    private static string ProposalJson<T>(T value) => JsonSerializer.Serialize(value, ProposalStorageOptions);
    private static T ProposalRead<T>(string json) => JsonSerializer.Deserialize<T>(json, ProposalStorageOptions)!;
    private static DreamProposalView ProposalView(ProposalRow row) => new(row.Id, row.ProjectId, row.RunId,
        ParseName<DreamProposalAction>(row.Action), row.TargetId, row.TargetVersion, row.Rationale, row.Prevalence,
        ProposalRead<List<DreamEvidenceView>>(row.EvidenceJson), ParseName<DreamProposalStatus>(row.Status), row.Version,
        ProposalRead<WorkActor>(row.AuthorJson), Utc(row.CreatedAt), row.OtherId, row.OtherVersion, row.Draft,
        row.ReplacementInputJson is { } input ? ProposalRead<KnowledgeEntryInput>(input) : null, row.ReviewReason,
        row.ReviewedByJson is { } actor ? ProposalRead<WorkActor>(actor) : null, row.ReviewedAt is { } at ? Utc(at) : null);

    public async Task<DreamProposalView?> GetDreamProposalAsync(Guid projectId, Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
            "SELECT * FROM dream_proposals WHERE project_id=@projectId AND id=@id", new { projectId, id }, cancellationToken: ct));
        return row is null ? null : ProposalView(row);
    }

    public Task<DreamProposalResult> CreateDreamProposalAsync(Guid projectId, Guid runId, DreamProposalSpec spec, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([DreamProposalLockName], async connection =>
        {
            DreamPolicy.RequireProject(actor, projectId);
            if (!IsDreamer(actor) || DreamPolicy.RequireScope(actor).RunId != runId || actor.SessionId != runId.ToString())
                throw WorkException.Invalid("Only the dreamer of this run can submit a dream proposal.");
            if (!Enum.IsDefined(spec.Action) || spec.Rationale.Length is 0 or > 1000 || spec.Prevalence.Length is 0 or > 1000 || spec.Evidence.Count is < 1 or > 50)
                throw WorkException.Invalid("A proposal needs a valid action, rationale, prevalence and 1 to 50 evidence references.");
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var project = await HoldProjectAsync(connection, tx, projectId, ct);
            await RequireProposalRunAsync(connection, tx, projectId, runId, ct);
            var target = await ReadKnowledgeRowAsync(connection, tx, spec.Target, ct);
            if (target is null || target.ProjectId != projectId)
                throw WorkException.Invalid("The proposal target must belong to the run's project.");
            RequireProposalVersion(target, spec.ExpectedVersion);
            if (target.Status != "active") throw WorkException.Invalid("A proposal target must be active.");
            if ((target.DetailsJson is { } detailJson ? ProposalRead<KnowledgeDetails>(detailJson).Source?.Kind : null) == CorrectionSourceKind.Prompt && spec.Action is DreamProposalAction.Retract or DreamProposalAction.Supersede)
                throw WorkException.Invalid("Changes to prompt-sourced lessons require an open challenge, not a proposal.");
            var draft = spec.Action is DreamProposalAction.SkillDraft or DreamProposalAction.HookDraft or DreamProposalAction.TestDraft;
            if ((spec.Action == DreamProposalAction.Supersede) != (spec.Replacement is not null) ||
                (spec.Action == DreamProposalAction.Merge ? spec.Other is null || spec.OtherVersion is null : spec.Other is not null || spec.OtherVersion is not null) ||
                draft != !string.IsNullOrWhiteSpace(spec.Draft) || spec.Draft?.Length > 100_000)
                throw WorkException.Invalid("supersede requires replacement; merge requires other and other_version; draft actions require draft text only.");
            if (spec.Replacement is { } replacement && (replacement.Supersedes is not null || replacement.SameAs is not null || replacement.Contradicts is not null))
                throw WorkException.Invalid("A proposal replacement cannot embed lifecycle actions.");
            if (spec.Replacement is { } next)
            {
                await ResolveScopeAsync(connection, tx, project, next.Repositories ?? [], next.Nodes, ct);
                if (next.ItemId is { } item && await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT project_id FROM work_items WHERE id=@item", new { item }, tx, cancellationToken: ct)) != projectId)
                    throw WorkException.Invalid("A replacement's item must belong to the proposal's project.");
            }
            if (spec.Other is { } other)
            {
                var destination = await ReadKnowledgeRowAsync(connection, tx, other, ct);
                if (other == spec.Target || target.Kind != "lesson" || destination is null || destination.ProjectId != projectId || destination.Kind != "lesson" || destination.Status != "active")
                    throw WorkException.Invalid("A merge needs two different active lessons in the run's project.");
                RequireProposalVersion(destination, spec.OtherVersion!.Value);
            }
            if (draft && (target.Kind != "lesson" || target.RuleState != "rule" || !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM lesson_events WHERE entry_id=@id AND rule_at_event=1 AND served=1)", new { id = target.Id }, tx, cancellationToken: ct))))
                throw WorkException.Invalid("A skill, hook or test draft needs a rule that recurred while served.");
            var evidence = new List<DreamEvidenceView>();
            foreach (var reference in spec.Evidence.Distinct()) evidence.Add(await ResolveDreamEvidenceAsync(connection, tx, projectId, reference, ct));
            var pending = await connection.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
                "SELECT * FROM dream_proposals WHERE project_id=@projectId AND target_id=@target AND action=@action AND status='pending' LIMIT 1",
                new { projectId, target = spec.Target, action = JsonName(spec.Action) }, tx, cancellationToken: ct));
            if (pending is not null) { await tx.CommitAsync(ct); return new DreamProposalResult(false, false, ProposalView(pending)); }
            var rejected = await connection.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
                "SELECT * FROM dream_proposals WHERE project_id=@projectId AND target_id=@target AND action=@action AND status='rejected' ORDER BY reviewed_at DESC,id DESC LIMIT 1",
                new { projectId, target = spec.Target, action = JsonName(spec.Action) }, tx, cancellationToken: ct));
            if (rejected is not null)
            {
                var prior = ProposalRead<List<DreamEvidenceView>>(rejected.EvidenceJson);
                // Evidence dates and graph high-water marks come from the database. Rewording
                // rationale, reordering refs, or adding pre-existing uncited evidence cannot reset a rejection.
                bool IsNewEvidence(DreamEvidenceView ev)
                {
                    if (prior.Any(old => old.Kind == ev.Kind && old.Id == ev.Id && old.Fingerprint == ev.Fingerprint)) return false;
                    if (ev.Kind == DreamEvidenceKind.GraphNode && !prior.Any(old => old.Kind == ev.Kind && old.Id == ev.Id))
                        return long.TryParse(ev.Id, out var node) && node > rejected.RejectionGraphHead.GetValueOrDefault(long.MaxValue);
                    return ev.ObservedAt > Utc(rejected.ReviewedAt!.Value);
                }
                if (!evidence.Any(IsNewEvidence))
                { await tx.CommitAsync(ct); return new DreamProposalResult(false, true, ProposalView(rejected)); }
            }
            var id = Guid.NewGuid(); var now = Now();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO dream_proposals (id,project_id,run_id,action,target_id,target_version,other_id,other_version,rationale,prevalence,evidence_json,replacement_json,replacement_input_json,draft,author_json,created_at) " +
                "VALUES (@id,@projectId,@runId,@action,@target,@version,@other,@otherVersion,@rationale,@prevalence,@evidence,@replacement,@input,@draft,@author,@now)",
                new { id, projectId, runId, action = JsonName(spec.Action), target = spec.Target, version = spec.ExpectedVersion, other = spec.Other, otherVersion = spec.OtherVersion,
                    rationale = spec.Rationale, prevalence = spec.Prevalence, evidence = ProposalJson(evidence), replacement = spec.Replacement is null ? null : ProposalJson(spec.Replacement),
                    input = spec.ReplacementInput is null ? null : ProposalJson(spec.ReplacementInput), draft = spec.Draft, author = ProposalJson(actor), now }, tx, cancellationToken: ct));
            var stored = (await connection.QuerySingleAsync<ProposalRow>(new CommandDefinition("SELECT * FROM dream_proposals WHERE id=@id", new { id }, tx, cancellationToken: ct)));
            await tx.CommitAsync(ct);
            return new DreamProposalResult(true, false, ProposalView(stored));
        }, ct);

    private static void RequireProposalVersion(KnowledgeEntryRow row, long version)
    {
        if (row.Version != version) throw new WorkException(WorkErrorCodes.Conflict, $"Entry {row.Id} is at version {row.Version}, not {version}. Read it again before proposing or accepting.");
    }

    private static async Task<DreamEvidenceView> ResolveDreamEvidenceAsync(MySqlConnection c, MySqlTransaction tx, Guid projectId, DreamEvidenceInput input, CancellationToken ct)
    {
        if (input.Kind == DreamEvidenceKind.GraphNode ? !long.TryParse(input.Id, out var nodeId) || nodeId <= 0 : !Guid.TryParse(input.Id, out _))
            throw WorkException.Invalid("Evidence id must be a positive graph node id or a stored entry/event/item/review UUID.");
        string sql = input.Kind switch
        {
            DreamEvidenceKind.Entry => "SELECT SHA2(CONCAT_WS('|',kind,status,summary,body,details_json),256) fingerprint,updated_at observed_at FROM knowledge_entries WHERE id=@id AND project_id=@projectId",
            DreamEvidenceKind.LessonEvent => "SELECT event_hash fingerprint,created_at observed_at FROM lesson_events WHERE correction_id=@id AND project_id=@projectId",
            DreamEvidenceKind.Item => "SELECT CAST(version AS CHAR) fingerprint,updated_at observed_at FROM work_items WHERE id=@id AND project_id=@projectId",
            DreamEvidenceKind.ReviewEvidence => "SELECT SHA2(CONCAT_WS('|',e.body,e.review_state,e.evidence_key),256) fingerprint,e.updated_at observed_at FROM work_github_evidence e JOIN work_items i ON i.id=e.item_id WHERE e.id=@id AND i.project_id=@projectId",
            DreamEvidenceKind.GraphNode => "SELECT SHA2(CONCAT_WS('|',n.qualified_name,n.file_path,n.properties,f.content_hash),256) fingerprint,COALESCE(r.indexed_at,r.created_at) observed_at FROM nodes n JOIN work_project_repositories p ON p.repository_name=n.repository JOIN repositories r ON r.name=n.repository LEFT JOIN file_hashes f ON f.repository=n.repository AND f.rel_path=n.file_path WHERE n.id=@id AND p.project_id=@projectId",
            _ => throw WorkException.Invalid("Unknown proposal evidence kind.")
        };
        var value = await c.QuerySingleOrDefaultAsync<EvidenceSnapshot>(new CommandDefinition(sql, new { id = input.Id, projectId }, tx, cancellationToken: ct));
        if (value is null) throw WorkException.Invalid($"Evidence {JsonName(input.Kind)} {input.Id} does not exist in the run's project.");
        return new DreamEvidenceView(input.Kind, input.Id, value.Fingerprint, Utc(value.ObservedAt));
    }
    private sealed class EvidenceSnapshot { public string Fingerprint { get; init; } = ""; public DateTime ObservedAt { get; init; } }

    private static async Task RequireProposalRunAsync(MySqlConnection c, MySqlTransaction tx, Guid projectId, Guid runId, CancellationToken ct)
    {
        if (!await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dreaming_runs WHERE id=@runId AND project_id=@projectId AND status='running')", new { runId, projectId }, tx, cancellationToken: ct)))
            throw WorkException.Invalid("The proposal must belong to an active dreaming run in this project.");
    }

    public async Task<DreamReviewResult> ReviewDreamProposalAsync(Guid projectId, Guid id, long expectedVersion, DreamReviewAction action, string? reason, WorkActor actor, CancellationToken ct = default)
    {
        var result = await ReviewDreamProposalCoreAsync(projectId, id, expectedVersion, action, reason, actor, ct);
        if (result.Proposal.Action != DreamProposalAction.Supersede || result.Entry is not { } entry) return result;
        // Match ordinary record's post-commit baseline adoption when a first index races acceptance.
        await AdoptPendingAsync([entry.Id], ct);
        return result with { Entry = await GetKnowledgeAsync(entry.Id, false, ct) ?? entry };
    }

    private Task<DreamReviewResult> ReviewDreamProposalCoreAsync(Guid projectId, Guid id, long expectedVersion, DreamReviewAction action, string? reason, WorkActor actor, CancellationToken ct) =>
        RunAsync([DreamProposalLockName, "codegraph_lesson_merges"], async c =>
        {
            DreamPolicy.Refuse(actor, "accept or reject proposals");
            if (!Enum.IsDefined(action) || action == DreamReviewAction.Reject && string.IsNullOrWhiteSpace(reason) || reason?.Length > 1000)
                throw WorkException.Invalid("Rejecting a proposal requires a reason of at most 1000 characters.");
            await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var project = await HoldProjectAsync(c, tx, projectId, ct);
            var row = await c.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition("SELECT * FROM dream_proposals WHERE id=@id AND project_id=@projectId FOR UPDATE", new { id, projectId }, tx, cancellationToken: ct))
                ?? throw WorkException.NotFound("There is no proposal in this project with that id.");
            if (row.Version != expectedVersion || row.Status != "pending")
                throw new WorkException(WorkErrorCodes.Conflict, "The proposal changed after it was read, or has already been reviewed.", ProposalView(row));
            var now = Now(); KnowledgeView? entry = null;
            if (action == DreamReviewAction.Accept)
            {
                await c.ExecuteAsync(new CommandDefinition("SELECT id FROM knowledge_entries WHERE id IN @ids ORDER BY id FOR UPDATE",
                    new { ids = new[] { row.TargetId.ToString(), row.OtherId?.ToString() }.OfType<string>().ToArray() }, tx, cancellationToken: ct));
                var target = await ReadKnowledgeRowAsync(c, tx, row.TargetId, ct) ?? throw WorkException.NotFound("The proposal target no longer exists.");
                RequireProposalVersion(target, row.TargetVersion);
                if (target.Status != "active") throw new WorkException(WorkErrorCodes.Conflict, "The proposal target is no longer active.");
                switch (ParseName<DreamProposalAction>(row.Action))
                {
                    case DreamProposalAction.Retract:
                        var advanced = await AdvanceKnowledgeAsync(c, tx, row.TargetId, row.TargetVersion, now, ct);
                        await c.ExecuteAsync(new CommandDefinition("UPDATE knowledge_entries SET status='retracted',status_reason=@reason WHERE id=@id", new { id = row.TargetId, reason = row.Rationale }, tx, cancellationToken: ct));
                        await InsertRevisionAsync(c, tx, row.TargetId, advanced.Revision, "retracted", actor, now, ct);
                        entry = await LoadKnowledgeViewAsync(c, tx, row.TargetId, false, ct);
                        break;
                    case DreamProposalAction.Supersede:
                        var replacement = ProposalRead<KnowledgeSpec>(row.ReplacementJson!) with { Supersedes = new SupersedeSpec(row.TargetId, row.TargetVersion) };
                        await InsertKnowledgeAsync(c, tx, project, [replacement], null, null, null, actor, now, ct);
                        entry = await LoadKnowledgeViewAsync(c, tx, replacement.Id, false, ct);
                        break;
                    case DreamProposalAction.Merge:
                        var other = await ReadKnowledgeRowAsync(c, tx, row.OtherId!.Value, ct) ?? throw WorkException.NotFound("The merge destination no longer exists.");
                        RequireProposalVersion(other, row.OtherVersion!.Value);
                        entry = await AcceptProposalMergeAsync(c, tx, row.TargetId, row.TargetVersion, other.Id, actor, now, ct);
                        break;
                    // Draft acceptance only records review attribution. No file write, command,
                    // addressed_by change, or automatic challenge resolution is performed.
                }
            }
            long? graphHead = action == DreamReviewAction.Reject
                ? await c.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COALESCE(MAX(n.id),0) FROM nodes n JOIN work_project_repositories p ON p.repository_name=n.repository WHERE p.project_id=@projectId", new { projectId }, tx, cancellationToken: ct)) : null;
            await c.ExecuteAsync(new CommandDefinition("UPDATE dream_proposals SET status=@status,version=version+1,review_reason=@reason,reviewed_by_json=@actor,reviewed_at=@now,rejection_graph_head=@graphHead WHERE id=@id",
                new { id, status = action == DreamReviewAction.Accept ? "accepted" : "rejected", reason, actor = ProposalJson(actor), now, graphHead }, tx, cancellationToken: ct));
            var result = await c.QuerySingleAsync<ProposalRow>(new CommandDefinition("SELECT * FROM dream_proposals WHERE id=@id", new { id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new DreamReviewResult(ProposalView(result), entry);
        }, ct);

    private Task<KnowledgeView> AcceptProposalMergeAsync(MySqlConnection c, MySqlTransaction tx, Guid target, long version, Guid other, WorkActor actor, DateTime now, CancellationToken ct) =>
        RelateLessonCoreAsync(c, tx, target, version, other, null, actor, now, ct);

    private async Task AddDreamProposalQueueAsync(MySqlConnection connection, Guid projectId, List<ReviewItem> items, CancellationToken ct)
    {
        var proposals = await connection.QueryAsync<ProposalRow>(new CommandDefinition("SELECT * FROM dream_proposals WHERE project_id=@projectId AND status='pending' ORDER BY created_at,id" + ReviewCandidateLimit, new { projectId }, cancellationToken: ct));
        var rows = proposals.ToList();
        if (rows.Count == 0) return;
        var targets = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition(
            $"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.id IN @ids", new { ids = rows.Select(row => row.TargetId.ToString()).Distinct().ToArray() }, cancellationToken: ct))).ToDictionary(row => row.Id);
        foreach (var row in rows)
            if (targets.TryGetValue(row.TargetId, out var target))
                items.Add(new ReviewItem(ReviewKinds.DreamProposal, ToRow(target), target.Priority ?? "normal", target.Recurrence ?? 1, Utc(row.CreatedAt), Author: ProposalRead<WorkActor>(row.AuthorJson), Proposal: ProposalView(row)));
    }
}
