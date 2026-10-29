using CodeGraph.Data;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Session retention (sc-27882).
public sealed partial class MySqlWorkStore : IWorkSessionPruner
{
    // A session is prunable when it is old, holds no open claim row, and nothing durable names it.
    private const string PrunableSessions =
        "COALESCE(s.ended_at, s.last_seen_at) < @cutoff " +
        "AND NOT EXISTS (SELECT 1 FROM work_session_claims c WHERE c.session_id = s.id AND c.released_at IS NULL) " +
        "AND NOT EXISTS (SELECT 1 FROM work_changes x WHERE x.actor_session_id = s.id) " +
        "AND NOT EXISTS (SELECT 1 FROM work_handoffs x WHERE x.author_session_id = s.id) " +
        "AND NOT EXISTS (SELECT 1 FROM work_item_comments x WHERE x.author_session_id = s.id) " +
        "AND NOT EXISTS (SELECT 1 FROM work_items x WHERE x.created_session_id = s.id OR x.claim_session_id = s.id) " +
        "AND NOT EXISTS (SELECT 1 FROM work_features x WHERE x.created_session_id = s.id) " +
        "AND NOT EXISTS (SELECT 1 FROM work_projects x WHERE x.created_session_id = s.id)";

    public async Task<WorkSessionPruneResult> PruneSessionsAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 10_000);
        int sessions = 0, claims = 0, keys = 0;

        // Closed claim rows of every session past the window go, kept session or not: they only feed the
        // recovery fallback "who last held this item", which is meaningless after the retention window.
        while (true)
        {
            var deleted = await RunAsync([], async connection => await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM work_session_claims WHERE released_at IS NOT NULL AND session_id IN " +
                "(SELECT s.id FROM work_sessions s WHERE COALESCE(s.ended_at, s.last_seen_at) < @cutoff) " +
                $"LIMIT {batchSize}",
                new { cutoff = cutoffUtc }, cancellationToken: ct)), ct);
            claims += deleted;
            if (deleted < batchSize)
                break;
        }

        while (true)
        {
            var batch = await RunAsync([], async connection =>
            {
                await using var transaction = await connection.BeginTransactionAsync(ct);
                var ids = (await connection.QueryAsync<string>(new CommandDefinition(
                    $"SELECT s.id FROM work_sessions s WHERE {PrunableSessions} ORDER BY s.id LIMIT {batchSize} FOR UPDATE",
                    new { cutoff = cutoffUtc }, transaction, cancellationToken: ct))).ToList();
                if (ids.Count == 0)
                {
                    await transaction.CommitAsync(ct);
                    return (Found: 0, Sessions: 0, Keys: 0);
                }

                // The candidates are locked, so none can gain a heartbeat before the delete, and none has a claim
                // row left (open rows keep a session out of the set; the FK would cascade only closed ones). The
                // start_session idempotency keys go too, or a replay would find a session that is gone.
                var removedKeys = await connection.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM work_idempotency_keys WHERE operation = '{SessionOperation}' AND entity_id IN @ids",
                    new { ids }, transaction, cancellationToken: ct));
                var removed = await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM work_sessions WHERE id IN @ids",
                    new { ids }, transaction, cancellationToken: ct));
                await transaction.CommitAsync(ct);
                return (Found: ids.Count, Sessions: removed, Keys: removedKeys);
            }, ct);

            sessions += batch.Sessions;
            keys += batch.Keys;
            if (batch.Found < batchSize)
                break;
        }

        return new WorkSessionPruneResult(sessions, claims, keys);
    }
}
