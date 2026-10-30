using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore
{
    public async Task<DreamKnowledgeDigest> ReadDreamDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var entries = (await connection.QueryAsync<KnowledgeEntryRow>(new CommandDefinition($"SELECT {KnowledgeColumns} {KnowledgeFrom} WHERE e.project_id=@projectId ORDER BY e.id", new { projectId }, cancellationToken: ct))).ToList();
        var events = (await connection.QueryAsync(new CommandDefinition("SELECT correction_id,entry_id,event_hash,counted,rule_at_event,served,created_at FROM lesson_events WHERE project_id=@projectId ORDER BY correction_id", new { projectId }, cancellationToken: ct))).ToArray();
        var reviews = (await connection.QueryAsync(new CommandDefinition("SELECT r.id,r.updated_at FROM work_github_evidence r JOIN work_items i ON i.id=r.item_id WHERE i.project_id=@projectId ORDER BY r.id", new { projectId }, cancellationToken: ct))).ToArray();
        var challenges = (await connection.QueryAsync(new CommandDefinition("SELECT id,entry_id,status,created_at,resolved_at FROM lesson_challenges WHERE project_id=@projectId ORDER BY id", new { projectId }, cancellationToken: ct))).ToArray();
        var freshness = (await connection.QueryAsync(new CommandDefinition("SELECT k.entry_id,k.repository_name,k.path,k.content_hash,h.content_hash AS current_hash FROM knowledge_entry_files k JOIN knowledge_entries e ON e.id=k.entry_id LEFT JOIN file_hashes h ON h.repository=k.repository_name AND h.rel_path=k.path WHERE e.project_id=@projectId ORDER BY k.entry_id,k.repository_name,k.path", new { projectId }, cancellationToken: ct))).ToArray();
        // Stable state fingerprint also detects newly stale files, removed graph dependencies and
        // edits of review evidence. The run service compares it to the last completed run.
        var fingerprint = HashUrl(JsonSerializer.Serialize(new { entries = entries.Select(row => new { row.Id, row.Version, row.Status, row.Stale, row.RuleState }), events, reviews, challenges, freshness }));
        var changed = entries.Where(row => since is null || row.UpdatedAt > since).ToList();
        var stale = entries.Where(row => row.PossiblyStale && row.Status == "active").ToList();
        var hasSignal = changed.Count > 0 || stale.Count > 0 || events.Any(row => since is null || (DateTime)row.created_at > since) || reviews.Any(row => since is null || (DateTime)row.updated_at > since) || challenges.Any(row => since is null || (DateTime)row.created_at > since);
        var data = JsonSerializer.SerializeToElement(new
        {
            untrusted_data = true, project_id = projectId, since,
            changed_count = changed.Count, changed_entries = changed.Take(100).Select(row => ToRow(row)), changed_more = changed.Count > 100,
            stale_count = stale.Count, possibly_stale = stale.Take(100).Select(row => ToRow(row)), stale_more = stale.Count > 100,
            lesson_event_count = events.Length, review_evidence_count = reviews.Length,
            available_reads = new[] { "dream_read", "dream_lessons", "dream_similar", "dream_review_queue", "dream_work_trail", "dream_search_graph", "dream_read_source" }
        }, WorkJson.Options);
        return new(data, fingerprint, hasSignal);
    }

    public async Task<object> ReadDreamSimilarAsync(Guid projectId, int offset, int limit, CancellationToken ct = default)
    {
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

    public async Task<object> ReadDreamWorkTrailAsync(Guid projectId, DateTime? since, int offset, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = (await connection.QueryAsync(new CommandDefinition("""
            SELECT * FROM (
              SELECT 'attempt' kind,a.id,a.item_id,a.started_at created_at,JSON_OBJECT('branch',a.branch,'outcome',a.outcome,'reason',a.reason,'ended_at',a.ended_at) data
                FROM work_attempts a JOIN work_items i ON i.id=a.item_id WHERE i.project_id=@projectId
              UNION ALL SELECT 'handoff',h.id,h.item_id,h.created_at,JSON_OBJECT('done',h.done,'decisions',h.decisions_json,'open_issues',h.open_issues_json,'corrections',h.corrections_json,'next_action',h.next_action)
                FROM work_handoffs h JOIN work_items i ON i.id=h.item_id WHERE i.project_id=@projectId
              UNION ALL SELECT 'thread',c.id,c.item_id,c.created_at,JSON_OBJECT('body',c.body,'author',c.author)
                FROM work_item_comments c JOIN work_items i ON i.id=c.item_id WHERE i.project_id=@projectId
              UNION ALL SELECT 'message',m.id,m.item_id,m.created_at,JSON_OBJECT('body',m.body,'kind',m.kind,'author',m.sender_name)
                FROM work_messages m WHERE m.project_id=@projectId
              UNION ALL SELECT 'review',r.id,r.item_id,r.updated_at,JSON_OBJECT('kind',r.kind,'body',r.body,'url',r.url,'source',r.source,'author',r.author_login)
                FROM work_github_evidence r JOIN work_items i ON i.id=r.item_id WHERE i.project_id=@projectId
            ) trail WHERE @since IS NULL OR created_at>@since ORDER BY created_at,id,kind LIMIT @take OFFSET @offset
            """, new { projectId, since, offset, take = limit + 1 }, cancellationToken: ct))).ToList();
        return new { items = rows.Take(limit), more = rows.Count > limit, next_offset = rows.Count > limit ? offset + limit : (int?)null };
    }

    public async Task ValidateDreamEvidenceAsync(Guid projectId, IReadOnlyList<DreamEvidence> evidence, CancellationToken ct = default)
    {
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
