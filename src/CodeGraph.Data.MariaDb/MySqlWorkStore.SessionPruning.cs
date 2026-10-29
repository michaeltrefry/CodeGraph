using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Session retention (sc-27882).
public sealed partial class MySqlWorkStore : IWorkSessionPruner
{
    // A session is prunable when it ended or went idle before the cutoff and nothing still needs the row itself:
    // no open claim row, no item whose claim names it, no live hold or queued waiter, and no direct message
    // that a later session may still inherit. History that names it (change feed, handoffs, comments,
    // messages, attempts, holds, knowledge) keeps its harness, model and host, because they are copied onto
    // those rows (migration 085) in the transaction that deletes the session.
    private const string PrunableSessions =
        "COALESCE(s.ended_at, s.last_seen_at) < @cutoff " +
        "AND NOT EXISTS (SELECT 1 FROM work_session_claims c WHERE c.session_id = s.id AND c.released_at IS NULL) " +
        "AND NOT EXISTS (SELECT 1 FROM work_items x WHERE x.claim_session_id = s.id) " +
        "AND NOT EXISTS (SELECT 1 FROM work_holds h WHERE h.session_id = s.id AND " +
        "((h.state = 'held' AND h.expires_at > @now) OR (h.state = 'waiting' AND h.wait_expires_at > @now))) " +
        "AND NOT (COALESCE(s.ended_at, s.last_seen_at) > @inheritFrom AND EXISTS (" +
        "SELECT 1 FROM work_message_deliveries d JOIN work_messages m ON m.id = d.message_id " +
        "WHERE d.recipient = s.id AND d.handed_to IS NULL AND m.item_id IS NULL AND m.to_session_id IS NOT NULL " +
        "AND (d.read_at IS NULL OR m.state = 'open')))";

    // (table, session id column, harness, model, host) -- the attribution copied onto rows before their session goes.
    private static readonly (string Table, string Session, string Harness, string Model, string Host)[] AttributedColumns =
    [
        ("work_changes", "actor_session_id", "actor_harness", "actor_model", "actor_host"),
        ("work_handoffs", "author_session_id", "author_harness", "author_model", "author_host"),
        ("work_item_comments", "author_session_id", "author_harness", "author_model", "author_host"),
        ("work_messages", "sender_session_id", "sender_harness", "sender_model", "sender_host"),
        ("work_messages", "resolved_by_session_id", "resolved_by_harness", "resolved_by_model", "resolved_by_host"),
        ("work_message_events", "actor_session_id", "actor_harness", "actor_model", "actor_host"),
        ("work_attempts", "session_id", "harness", "model", "host"),
        ("work_holds", "session_id", "harness", "model", "host"),
        ("knowledge_revisions", "author_session_id", "author_harness", "author_model", "author_host"),
    ];

    public async Task<WorkSessionPruneResult> PruneSessionsAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 10_000);
        int sessions = 0, claims = 0, keys = 0;
        var now = Now();

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
                    new { cutoff = cutoffUtc, now, inheritFrom = now - WorkMessageLimits.DirectMessageRetention }, transaction, cancellationToken: ct))).ToList();
                if (ids.Count == 0)
                {
                    await transaction.CommitAsync(ct);
                    return (Found: 0, Sessions: 0, Keys: 0);
                }

                // The candidates are locked, so none can gain a heartbeat before the delete, and none has a claim
                // row left (open rows keep a session out of the set; the FK would cascade only closed ones).
                // Attribution is copied onto the rows that name the session first; the start_session idempotency
                // keys go too, or a replay would find a session that is gone.
                foreach (var (table, session, harness, model, host) in AttributedColumns)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"UPDATE {table} t JOIN work_sessions s ON s.id = t.{session} " +
                        $"SET t.{harness} = s.harness, t.{model} = s.model, t.{host} = s.host WHERE t.{session} IN @ids",
                        new { ids }, transaction, cancellationToken: ct));
                }

                // A notice for a session that no longer exists can never be delivered.
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM work_notices WHERE session_id IN @ids", new { ids }, transaction, cancellationToken: ct));
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
