using CodeGraph.Data;
using Dapper;

namespace CodeGraph.Data.MariaDb;

// Retention of messages (sc-27887). What is kept, and why:
//  - an open question or request (state 'open'): it still waits for an answer, and the item view
//    shows it (open_messages);
//  - a message with any delivery not read, or read or created after the cutoff: an unread one is in
//    an inbox, and a direct message with no item is inherited only while a delivery is unread or the
//    message is open, so an inheritable message always has such a delivery or is open;
//  - every message of an item that is not complete or closed: the thread of a live item is the history
//    the UI and a session taking the item over read;
//  - the message of the newest delivery and the newest event row: the heads (MAX(sequence)) that
//    validate a cursor stay as they were. AUTO_INCREMENT counters never move back when rows go
//    (InnoDB persists them), so no sequence is reused and a replay neither skips nor repeats.
// Each batch runs in a transaction under the messages lock, taken with no row lock held (the lock
// order of MySqlWorkStore.Messages.cs); the lock keeps posters from inserting a delivery between the
// look and the delete. Reads of work_items are plain consistent reads and take no row lock.
public sealed partial class MySqlWorkStore
{
    public async Task<MessageRetentionResult> PruneMessagesAsync(DateTime cutoff, int batchSize, CancellationToken ct = default)
    {
        batchSize = Math.Max(1, batchSize);
        int messages = 0, deliveries = 0, events = 0;

        var (keepMessage, keepEvent) = await ReadRetentionHeadsAsync(ct);

        long after = 0;
        while (true)
        {
            var batch = await RunAsync([], async connection =>
            {
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using var messageLock = new MessageLockScope(connection, transaction);
                await messageLock.AcquireAsync(ct);

                var candidates = (await connection.QueryAsync<(long Sequence, Guid Id)>(new CommandDefinition(
                    "SELECT m.sequence, m.id FROM work_messages m WHERE m.sequence > @after AND m.created_at < @cutoff " +
                    "AND (m.resolved_at IS NULL OR m.resolved_at < @cutoff) AND (m.state IS NULL OR m.state <> 'open') AND m.id <> @keepMessage " +
                    "AND NOT EXISTS (SELECT 1 FROM work_message_deliveries d WHERE d.message_id = m.id AND (d.read_at IS NULL OR d.read_at >= @cutoff OR d.created_at >= @cutoff)) " +
                    "AND NOT EXISTS (SELECT 1 FROM work_items i WHERE i.id = m.item_id AND i.status NOT IN ('complete', 'closed')) " +
                    "ORDER BY m.sequence LIMIT @batchSize",
                    new { after, cutoff, keepMessage, batchSize }, transaction, cancellationToken: ct))).ToList();

                int deletedDeliveries = 0, deletedMessages = 0;
                if (candidates.Count > 0)
                {
                    var ids = candidates.Select(entry => entry.Id).ToArray();
                    deletedDeliveries = await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM work_message_deliveries WHERE message_id IN @ids", new { ids }, transaction, cancellationToken: ct));
                    deletedMessages = await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM work_messages WHERE id IN @ids", new { ids }, transaction, cancellationToken: ct));
                }

                await transaction.CommitAsync(ct);
                return (Last: candidates.Count == 0 ? after : candidates[^1].Sequence, Count: candidates.Count, DeletedMessages: deletedMessages, DeletedDeliveries: deletedDeliveries);
            }, ct);

            messages += batch.DeletedMessages;
            deliveries += batch.DeletedDeliveries;
            after = batch.Last;
            if (batch.Count < batchSize)
                break;
        }

        // The oldest event rows are the deletable ones (sequence order is time order).
        while (true)
        {
            var deleted = await RunAsync([], async connection =>
            {
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using var messageLock = new MessageLockScope(connection, transaction);
                await messageLock.AcquireAsync(ct);
                var count = await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM work_message_events WHERE sequence < @keepEvent AND created_at < @cutoff ORDER BY sequence LIMIT @batchSize",
                    new { keepEvent, cutoff, batchSize }, transaction, cancellationToken: ct));
                await transaction.CommitAsync(ct);
                return count;
            }, ct);

            events += deleted;
            if (deleted < batchSize)
                break;
        }

        return new MessageRetentionResult(messages, deliveries, events);
    }

    private async Task<(Guid KeepMessage, long KeepEvent)> ReadRetentionHeadsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var message = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT message_id FROM work_message_deliveries ORDER BY sequence DESC LIMIT 1", cancellationToken: ct));
        var head = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT MAX(sequence) FROM work_message_events", cancellationToken: ct));
        return (message ?? Guid.Empty, head ?? 0);
    }
}
