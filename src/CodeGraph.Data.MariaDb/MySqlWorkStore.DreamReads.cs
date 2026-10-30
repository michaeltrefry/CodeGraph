using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore
{
    public async Task<DreamKnowledgeDigest> ReadDreamDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var connection = await OpenAsync(ct);
        var entries = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition($"SELECT {KnowledgeColumns.Replace("e.body, e.details_json,", "")} {KnowledgeFrom} WHERE e.project_id=@projectId ORDER BY e.id", new { projectId }, cancellationToken: ct))).ToList();
        var events = (await connection.QueryAsync(new CommandDefinition("SELECT correction_id,entry_id,event_hash,counted,rule_at_event,served,author,author_kind,session_id,created_at FROM lesson_events WHERE project_id=@projectId ORDER BY correction_id", new { projectId }, cancellationToken: ct))).ToArray();
        var reviews = (await connection.QueryAsync(new CommandDefinition("SELECT r.id,r.updated_at FROM work_github_evidence r JOIN work_items i ON i.id=r.item_id WHERE i.project_id=@projectId ORDER BY r.id", new { projectId }, cancellationToken: ct))).ToArray();
        var challenges = (await connection.QueryAsync(new CommandDefinition("SELECT id,entry_id,status,created_at,resolved_at,author,author_kind,resolved_by,resolved_by_kind,author_session_id FROM lesson_challenges WHERE project_id=@projectId ORDER BY id", new { projectId }, cancellationToken: ct))).ToArray();
        var proposals = (await connection.QueryAsync(new CommandDefinition("SELECT id,run_id,target_id,status,version,created_at,reviewed_at FROM dream_proposals WHERE project_id=@projectId ORDER BY id", new { projectId }, cancellationToken: ct))).ToArray();
        var trail = (await connection.QueryAsync(new CommandDefinition("SELECT m.id,m.created_at,m.resolved_at,m.state FROM work_messages m WHERE m.project_id=@projectId ORDER BY m.id", new { projectId }, cancellationToken: ct))).ToArray();
        var activity = (await connection.QueryAsync(new CommandDefinition("""
            SELECT 'attempt' kind,a.id,COALESCE(a.ended_at,a.started_at) changed_at,a.outcome state FROM work_attempts a JOIN work_items i ON i.id=a.item_id WHERE i.project_id=@projectId
            UNION ALL SELECT 'handoff',h.id,h.created_at,NULL FROM work_handoffs h JOIN work_items i ON i.id=h.item_id WHERE i.project_id=@projectId
            UNION ALL SELECT 'thread',c.id,c.created_at,NULL FROM work_item_comments c JOIN work_items i ON i.id=c.item_id WHERE i.project_id=@projectId
            ORDER BY kind,id
            """, new { projectId }, cancellationToken: ct))).ToArray();
        var changedRevisions = (await connection.QueryAsync(new CommandDefinition("SELECT r.entry_id,r.revision,r.action,r.author,r.author_kind,r.created_at FROM knowledge_revisions r JOIN knowledge_entries e ON e.id=r.entry_id WHERE e.project_id=@projectId AND (@since IS NULL OR r.created_at>=@since) ORDER BY r.created_at DESC,r.entry_id,r.revision DESC", new { projectId, since }, cancellationToken: ct))).ToArray();

        var freshness = (await connection.QueryAsync(new CommandDefinition("SELECT k.entry_id,k.repository_name,k.path,k.content_hash,h.content_hash AS current_hash FROM knowledge_entry_files k JOIN knowledge_entries e ON e.id=k.entry_id LEFT JOIN file_hashes h ON h.repository=k.repository_name AND h.rel_path=k.path WHERE e.project_id=@projectId ORDER BY k.entry_id,k.repository_name,k.path", new { projectId }, cancellationToken: ct))).ToArray();
        // The watermark is the previous digest's start, not its completion: changes that arrive
        // while a model is working must remain eligible for the following pass. Dreamer output
        // is excluded from the signal identity so an unchanged project cannot self-trigger forever.
        var revisions=(await connection.QueryAsync(new CommandDefinition("SELECT r.entry_id,r.revision,r.action,r.created_at FROM knowledge_revisions r JOIN knowledge_entries e ON e.id=r.entry_id WHERE e.project_id=@projectId AND NOT (r.author_kind='system' AND r.author='dreamer' AND EXISTS (SELECT 1 FROM dreaming_runs d WHERE d.id=r.author_session_id AND d.status='completed')) ORDER BY r.entry_id,r.revision",new {projectId},cancellationToken:ct))).ToArray();
        var completedRuns=(await connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM dreaming_runs WHERE project_id=@projectId AND status='completed'",new {projectId},cancellationToken:ct))).ToHashSet();
        bool CompletedDreamOutput(dynamic row,string field)=> (string)row.author_kind=="system"&&(string)row.author=="dreamer"
            &&Guid.TryParse(((IDictionary<string,object>)row)[field]?.ToString(),out var id)&&completedRuns.Contains(id);
        var externalEvents=events.Where(row=>!CompletedDreamOutput(row,"session_id")).ToArray();
        var externalChallenges=challenges.Where(row=>!CompletedDreamOutput(row,"author_session_id")||row.resolved_at is not null).ToArray();
        var nodes=(await connection.QueryAsync(new CommandDefinition("SELECT k.entry_id,k.node_id,n.repository,n.qualified_name,n.file_path FROM knowledge_entry_nodes k JOIN knowledge_entries e ON e.id=k.entry_id LEFT JOIN nodes n ON n.id=k.node_id WHERE e.project_id=@projectId ORDER BY k.entry_id,k.node_id",new {projectId},cancellationToken:ct))).ToArray();
        var externalProposals=proposals.Where(row=>row.reviewed_at is not null||!Guid.TryParse(Convert.ToString((object?)row.run_id),out var id)||!completedRuns.Contains(id)).ToArray();
        var fingerprint=HashUrl(JsonSerializer.Serialize(new {revisions,events=externalEvents.Select(row=>new {correction_id=row.correction_id.ToString(),event_hash=(string)row.event_hash,created_at=(DateTime)row.created_at}),reviews,challenges=externalChallenges,proposals=externalProposals,trail,activity,
            freshness=freshness.Select(row=>new {entry_id=row.entry_id.ToString(),repository_name=(string)row.repository_name,path=(string)row.path,current_hash=(string?)row.current_hash}),nodes}));

        var changed=entries.Where(row=>since is null||row.UpdatedAt>=since).ToList();
        var stale=entries.Where(row=>row.PossiblyStale&&row.Status=="active").ToList();
        var hasSignal=revisions.Any(row=>since is null||(DateTime)row.created_at>=since)||stale.Count>0
            ||externalEvents.Any(row=>since is null||(DateTime)row.created_at>=since)||reviews.Any(row=>since is null||(DateTime)row.updated_at>=since)
            ||externalChallenges.Any(row=>since is null||(DateTime)row.created_at>=since||row.resolved_at is DateTime resolved&&resolved>=since)
            ||externalProposals.Any(row=>since is null||(DateTime)row.created_at>=since||row.reviewed_at is DateTime reviewed&&reviewed>=since)
            ||trail.Any(row=>since is null||(DateTime)row.created_at>=since||row.resolved_at is DateTime resolved&&resolved>=since)
            ||activity.Any(row=>since is null||(DateTime)row.changed_at>=since);
        var data = JsonSerializer.SerializeToElement(new
        {
            untrusted_data = true, project_id = projectId, since,
            changed_count = changed.Count, changed_entries = changed.Take(5).Select(row => ToRow(row)), changed_more = changed.Count > 5,
            stale_count = stale.Count, possibly_stale = stale.Take(5).Select(row => ToRow(row)), stale_more = stale.Count > 5,
            changed_revisions = changedRevisions.Take(5), changed_revisions_count = changedRevisions.Length, changed_revisions_more = changedRevisions.Length > 5,
            knowledge_count = entries.Count, lesson_event_count = events.Length, review_evidence_count = reviews.Length,
            open_challenge_count = challenges.Count(row => (string)row.status == "open"), open_proposal_count = proposals.Count(row => (string)row.status == "pending"),
            work_trail_count = changedRevisions.Length + activity.Count(row => since is null || (DateTime)row.changed_at >= since)
                + trail.Count(row => since is null || (DateTime)(row.resolved_at ?? row.created_at) >= since)
                + reviews.Count(row => since is null || (DateTime)row.updated_at >= since),
            available_reads = new[] { "dream_read", "dream_lessons", "dream_similar", "dream_review_queue", "dream_work_trail", "dream_search_graph", "dream_read_source" }
        }, WorkJson.Options);
        return new(data, fingerprint, hasSignal);
    }

    public async Task<object> ReadDreamSimilarAsync(Guid projectId, int offset, int limit, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var connection = await OpenAsync(ct);
        // Unlike interactive capture's bounded similarity pool, dreaming considers every active
        // lesson. Pairs are paged in stable id order and previously related pairs are omitted.
        var lessons = (await connection.QueryAsync<PoolLesson>(new CommandDefinition("SELECT id,summary,summary_hash,version,rule_state FROM knowledge_entries WHERE project_id=@projectId AND kind='lesson' AND status='active' ORDER BY id", new { projectId }, cancellationToken: ct))).ToArray();
        var links = (await connection.QueryAsync<(Guid EntryId, Guid OtherId)>(new CommandDefinition("SELECT l.entry_id,l.other_id FROM lesson_links l JOIN knowledge_entries e ON e.id=l.entry_id WHERE e.project_id=@projectId", new { projectId }, cancellationToken: ct))).ToHashSet();
        var pairs = new List<object>();
        var found = 0;
        for (var left = 0; left < lessons.Length; left++)
        for (var right = left + 1; right < lessons.Length; right++)
        {
            var a = lessons[left]; var b = lessons[right];
            if (links.Contains((a.Id, b.Id)) || links.Contains((b.Id, a.Id))) continue;
            var score = LessonSimilarity.Score(a.Summary, b.Summary);
            if (score < LessonSimilarity.CandidateThreshold) continue;
            if (found++ < offset) continue;
            pairs.Add(new { left = a.Id, right = b.Id, left_summary = a.Summary, right_summary = b.Summary, score, possible_contradiction = LessonSimilarity.Negates(a.Summary) != LessonSimilarity.Negates(b.Summary) });
            if (pairs.Count > limit) return new { items = pairs.Take(limit), more = true, next_offset = offset + limit };
        }
        return new { items = pairs, more = false, next_offset = (int?)null };
    }

    public async Task<JsonElement> ReadDreamKnowledgeVersionAsync(Guid projectId, Guid entryId, long version, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var connection = await OpenAsync(ct);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT r.snapshot_json FROM knowledge_revisions r JOIN knowledge_entries e ON e.id=r.entry_id WHERE e.project_id=@projectId AND r.entry_id=@entryId AND JSON_VALUE(r.snapshot_json,'$.version')=@version ORDER BY r.revision DESC LIMIT 1",
            new { projectId, entryId, version }, cancellationToken: ct));
        if (json is null) throw WorkException.NotFound("This knowledge version is no longer available in the project's history.");
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    // A single SELECT captures the membership/order and content identity of the requested
    // window. Keep keys/hashes only: full revision bodies are fetched only when a page needs one.
    private const string DreamTrailSql = """
        SELECT * FROM (
              SELECT 'revision' kind,CONCAT(r.entry_id,':',r.revision) id,NULL item_id,r.created_at,r.snapshot_json data FROM knowledge_revisions r JOIN knowledge_entries e ON e.id=r.entry_id WHERE e.project_id=@projectId
              UNION ALL SELECT 'attempt',a.id,a.item_id,COALESCE(a.ended_at,a.started_at),JSON_OBJECT('branch',a.branch,'outcome',a.outcome,'reason',a.reason,'ended_at',a.ended_at) data
                FROM work_attempts a JOIN work_items i ON i.id=a.item_id WHERE i.project_id=@projectId
              UNION ALL SELECT 'handoff',h.id,h.item_id,h.created_at,JSON_OBJECT('done',h.done,'decisions',h.decisions_json,'open_issues',h.open_issues_json,'corrections',h.corrections_json,'next_action',h.next_action)
                FROM work_handoffs h JOIN work_items i ON i.id=h.item_id WHERE i.project_id=@projectId
              UNION ALL SELECT 'thread',c.id,c.item_id,c.created_at,JSON_OBJECT('body',c.body,'author',c.author)
                FROM work_item_comments c JOIN work_items i ON i.id=c.item_id WHERE i.project_id=@projectId
              UNION ALL SELECT 'message',m.id,m.item_id,COALESCE(m.resolved_at,m.created_at),JSON_OBJECT('body',m.body,'kind',m.kind,'author',m.sender_name)
                FROM work_messages m WHERE m.project_id=@projectId
              UNION ALL SELECT 'review',r.id,r.item_id,r.updated_at,JSON_OBJECT('kind',r.kind,'body',r.body,'url',r.url,'source',r.source,'author',r.author_login)
                FROM work_github_evidence r JOIN work_items i ON i.id=r.item_id WHERE i.project_id=@projectId
        ) trail
        """;

    public async Task<IReadOnlyList<DreamTrailReference>> ListDreamWorkTrailAsync(Guid projectId, DateTime? since, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var connection = await OpenAsync(ct);
        return (await connection.QueryAsync<DreamTrailReference>(new CommandDefinition(
            "SELECT kind,id,item_id,created_at,SHA2(data,256) AS content_hash FROM (" + DreamTrailSql + ") t WHERE @since IS NULL OR created_at>=@since ORDER BY created_at,id,kind",
            new { projectId, since }, cancellationToken: ct))).ToList();
    }

    public async Task<JsonElement> ReadDreamWorkTrailItemAsync(Guid projectId, DreamTrailReference reference, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT kind,id,item_id,created_at,data,SHA2(data,256) AS content_hash FROM (" + DreamTrailSql + ") t WHERE kind=@kind AND id=@id",
            new { projectId, kind = reference.Kind, id = reference.Id }, cancellationToken: ct));
        if (row is null || !string.Equals((string)row.content_hash, reference.ContentHash, StringComparison.Ordinal))
            throw new WorkException(WorkErrorCodes.Conflict, "This work-trail item changed after the page window was captured. Start a new read without cursor to inspect its current state.");
        return JsonSerializer.SerializeToElement(new { kind = (string)row.kind, id = (string)row.id, item_id = row.item_id,
            created_at = row.created_at, data = JsonSerializer.Deserialize<JsonElement>((string)row.data) }, WorkJson.Options);
    }

    public async Task<object> ReadDreamWorkTrailAsync(Guid projectId, DateTime? since, int offset, int limit, CancellationToken ct = default)
    {
        var index = await ListDreamWorkTrailAsync(projectId, since, ct);
        var rows = new List<JsonElement>();
        foreach (var row in index.Skip(offset).Take(limit)) rows.Add(await ReadDreamWorkTrailItemAsync(projectId, row, ct));
        return new { items = rows, more = index.Count > offset + limit, next_offset = index.Count > offset + limit ? offset + limit : (int?)null };
    }

    public async Task ValidateDreamEvidenceAsync(Guid projectId, IReadOnlyList<DreamEvidence> evidence, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var connection = await OpenAsync(ct);
        foreach (var item in evidence)
        {
            var sql = item.Kind switch
            {
                "entry" => "SELECT COUNT(*) FROM knowledge_entries WHERE project_id=@projectId AND id=@reference",
                "lesson_event" => "SELECT COUNT(*) FROM lesson_events WHERE project_id=@projectId AND correction_id=@reference",
                "item" => "SELECT COUNT(*) FROM work_items WHERE project_id=@projectId AND id=@reference",
                "challenge" => "SELECT COUNT(*) FROM lesson_challenges WHERE project_id=@projectId AND id=@reference",
                "message" => "SELECT COUNT(*) FROM work_messages WHERE project_id=@projectId AND id=@reference",
                "graph_node" => "SELECT COUNT(*) FROM nodes n JOIN work_project_repositories r ON r.repository_name=n.repository WHERE r.project_id=@projectId AND n.id=@reference",
                "review" => "SELECT COUNT(*) FROM work_github_evidence r JOIN work_items i ON i.id=r.item_id WHERE i.project_id=@projectId AND r.id=@reference",
                "attempt" => "SELECT COUNT(*) FROM work_attempts a JOIN work_items i ON i.id=a.item_id WHERE i.project_id=@projectId AND a.id=@reference",
                "handoff" => "SELECT COUNT(*) FROM work_handoffs h JOIN work_items i ON i.id=h.item_id WHERE i.project_id=@projectId AND h.id=@reference",
                "thread" => "SELECT COUNT(*) FROM work_item_comments c JOIN work_items i ON i.id=c.item_id WHERE i.project_id=@projectId AND c.id=@reference",
                _ => throw WorkException.Invalid("Evidence kind must be entry, lesson_event, item, challenge, message, graph_node, review, attempt, handoff or thread.")
            };
            if (string.IsNullOrWhiteSpace(item.Summary) || item.Summary.Length > 1000 || string.IsNullOrWhiteSpace(item.Reference) || await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { projectId, reference = item.Reference }, cancellationToken: ct)) == 0)
                throw WorkException.Invalid("Every dream evidence reference must resolve within the run's project and include a summary.");
        }
    }
}
