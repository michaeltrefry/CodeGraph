using CodeGraph.Models.Work;
using Dapper;

namespace CodeGraph.Data.MariaDb;

// Work areas for overlap warnings (sc-27872 phase 2). Read-only and computed on read: a claim by
// a live session on an unfinished item with touches. Nothing is stored for the warnings
// themselves, so a warning disappears the moment either claim does.
public sealed partial class MySqlWorkStore
{
    private sealed class AreaRow
    {
        public string SessionId { get; init; } = "";
        public string Identity { get; init; } = "";
        public string Harness { get; init; } = "";
        public string? Model { get; init; }
        public string? Host { get; init; }
        public string ProjectKey { get; init; } = "";
        public int Number { get; init; }
        public string? RepositoryName { get; init; }
        public string? TouchesJson { get; init; }
    }

    private const int MaxLiveAreas = 200;

    public async Task<IReadOnlyList<WorkArea>> ReadLiveWorkAreasAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var now = Now();
        var rows = await connection.QueryAsync<AreaRow>(new CommandDefinition(
            "SELECT s.id AS session_id, s.identity, s.harness, s.model, s.host, p.project_key, i.number, i.repository_name, i.touches_json " +
            "FROM work_items i JOIN work_projects p ON p.id = i.project_id JOIN work_sessions s ON s.id = i.claim_session_id " +
            "WHERE i.project_id = @projectId AND i.claim_holder IS NOT NULL AND i.claim_expires_at > @now " +
            "AND i.status NOT IN ('complete', 'closed') AND i.touches_json IS NOT NULL AND i.touches_json <> '[]' " +
            $"AND s.ended_at IS NULL AND s.last_seen_at > @staleBefore ORDER BY i.number LIMIT {MaxLiveAreas}",
            new { projectId, now, staleBefore = StaleBefore(now) }, cancellationToken: ct));
        return rows
            .Select(row => new WorkArea(
                row.SessionId, row.Identity, row.Harness, WorkKeys.Item(row.ProjectKey, row.Number), row.RepositoryName,
                ReadList<string>(row.TouchesJson), row.Model, row.Host))
            .Where(area => area.Touches.Count > 0)
            .ToList();
    }
}
