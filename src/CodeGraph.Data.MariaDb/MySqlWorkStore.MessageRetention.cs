using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;

namespace CodeGraph.Data.MariaDb;

// Retention of messages (sc-27887). What is kept, and why:
//  - an open question or request (state 'open'): it still waits for an answer, and the item view
//    shows it (open_messages);
//  - a message with a delivery created or read after the cutoff, or a delivery still unread that
//    could be read: an unread delivery to a session that is live, to the human, or to a session
//    that ended or went idle less than max(retention, DirectMessageRetention) ago. An unread
//    delivery to a session dead for longer than that is ignored, unless the message is a direct
//    message with no item whose original recipient could still pass it to a new session
//    (inheritance window, 7 days from that session's end). So a message a new session can
//    still inherit is never deleted;
//  - every message of an item until the item has been complete or closed for longer than the
//    window (status finished AND updated_at older than the cutoff; a reopened item is not finished);
//  - the message of the newest delivery and the newest event row: the heads (MAX(sequence)) that
//    validate a cursor stay as they were. AUTO_INCREMENT counters never move back when rows go
//    (InnoDB persists them), so no sequence is reused and a replay neither skips nor repeats.
// Candidates are found WITHOUT the messages lock, in bounded sequence windows, so a big table never
// stalls posters. Each delete chunk then takes the messages lock briefly (no row lock held first, the
// lock order of MySqlWorkStore.Messages.cs), re-checks the same predicate on the chosen ids and
// deletes only those that still qualify. The lock keeps posters from inserting a delivery between the
// re-check and the delete. Reads of work_items and work_sessions are plain consistent reads.
public sealed partial class MySqlWorkStore
{
    private const int RetentionWindowFactor = 10;

    // Predicate over alias m, shared by the unlocked scan and the locked re-check.
    private const string PrunableMessage =
        "m.created_at < @cutoff AND (m.resolved_at IS NULL OR m.resolved_at < @cutoff) AND (m.state IS NULL OR m.state <> 'open') AND m.id <> @keepMessage " +
        "AND NOT EXISTS (SELECT 1 FROM work_message_deliveries d WHERE d.message_id = m.id AND (d.created_at >= @cutoff OR d.read_at >= @cutoff OR (d.read_at IS NULL AND NOT (" +
            "d.recipient <> @human AND NOT EXISTS (SELECT 1 FROM work_sessions s WHERE s.id = d.recipient AND COALESCE(s.ended_at, s.last_seen_at) >= @deadBefore) " +
            "AND NOT (m.item_id IS NULL AND m.to_session_id IS NOT NULL AND EXISTS (SELECT 1 FROM work_sessions o WHERE o.id = m.to_session_id AND COALESCE(o.ended_at, o.last_seen_at) > @inheritFrom))" +
        ")))) " +
        "AND NOT EXISTS (SELECT 1 FROM work_items i WHERE i.id = m.item_id AND (i.status NOT IN ('complete', 'closed') OR i.updated_at >= @cutoff))";

    public async Task<MessageRetentionResult> PruneMessagesAsync(DateTime cutoff, int batchSize, CancellationToken ct = default)
    {
        DreamPolicy.RefuseAmbient("prune messages");
        batchSize = Math.Max(1, batchSize);
        var window = (long)batchSize * RetentionWindowFactor;
        int messages = 0, deliveries = 0, events = 0;

        var inheritFrom = Now() - WorkMessageLimits.DirectMessageRetention;
        var deadBefore = cutoff < inheritFrom ? cutoff : inheritFrom;
        var human = MessageChannels.HumanRecipient;
        var (keepMessage, keepEvent, maxMessage) = await ReadRetentionHeadsAsync(ct);

        // The scan position only moves forward within a run.
        for (long after = 0; after < maxMessage; after += window)
        {
            List<Guid> candidates;
            await using (var scan = await OpenAsync(ct))
            {
                candidates = (await scan.QueryAsync<Guid>(new CommandDefinition(
                    $"SELECT m.id FROM work_messages m WHERE m.sequence > @after AND m.sequence <= @upTo AND {PrunableMessage} ORDER BY m.sequence",
                    new { after, upTo = after + window, cutoff, keepMessage, human, inheritFrom, deadBefore },
                    cancellationToken: ct))).ToList();
            }

            foreach (var chunk in candidates.Chunk(batchSize))
            {
                var ids = chunk;
                var (deletedMessages, deletedDeliveries) = await RunAsync([], async connection =>
                {
                    await using var transaction = await connection.BeginTransactionAsync(ct);
                    await using var messageLock = new MessageLockScope(connection, transaction);
                    await messageLock.AcquireAsync(ct);
                    var still = (await connection.QueryAsync<Guid>(new CommandDefinition(
                        $"SELECT m.id FROM work_messages m WHERE m.id IN @ids AND {PrunableMessage}",
                        new { ids, cutoff, keepMessage, human, inheritFrom, deadBefore },
                        transaction, cancellationToken: ct))).ToArray();
                    int dm = 0, dd = 0;
                    if (still.Length > 0)
                    {
                        dd = await connection.ExecuteAsync(new CommandDefinition(
                            "DELETE FROM work_message_deliveries WHERE message_id IN @still", new { still }, transaction, cancellationToken: ct));
                        dm = await connection.ExecuteAsync(new CommandDefinition(
                            "DELETE FROM work_messages WHERE id IN @still", new { still }, transaction, cancellationToken: ct));
                    }

                    await transaction.CommitAsync(ct);
                    return (dm, dd);
                }, ct);
                messages += deletedMessages;
                deliveries += deletedDeliveries;
            }
        }

        // Events: found unlocked in windows, deleted by sequence under the lock.
        for (long after = 0; after < keepEvent; after += window)
        {
            List<long> sequences;
            await using (var scan = await OpenAsync(ct))
            {
                sequences = (await scan.QueryAsync<long>(new CommandDefinition(
                    "SELECT sequence FROM work_message_events WHERE sequence > @after AND sequence <= @upTo AND sequence < @keepEvent AND created_at < @cutoff ORDER BY sequence",
                    new { after, upTo = after + window, keepEvent, cutoff }, cancellationToken: ct))).ToList();
            }

            foreach (var chunk in sequences.Chunk(batchSize))
            {
                var ids = chunk;
                events += await RunAsync([], async connection =>
                {
                    await using var transaction = await connection.BeginTransactionAsync(ct);
                    await using var messageLock = new MessageLockScope(connection, transaction);
                    await messageLock.AcquireAsync(ct);
                    var count = await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM work_message_events WHERE sequence IN @ids AND created_at < @cutoff", new { ids, cutoff }, transaction, cancellationToken: ct));
                    await transaction.CommitAsync(ct);
                    return count;
                }, ct);
            }
        }

        return new MessageRetentionResult(messages, deliveries, events);
    }

    private async Task<(Guid KeepMessage, long KeepEvent, long MaxMessage)> ReadRetentionHeadsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var message = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT message_id FROM work_message_deliveries ORDER BY sequence DESC LIMIT 1", cancellationToken: ct));
        var head = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT MAX(sequence) FROM work_message_events", cancellationToken: ct));
        var maxMessage = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT MAX(sequence) FROM work_messages", cancellationToken: ct));
        return (message ?? Guid.Empty, head ?? 0, maxMessage ?? 0);
    }
}
