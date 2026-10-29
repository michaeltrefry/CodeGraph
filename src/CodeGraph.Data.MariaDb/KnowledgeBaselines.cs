using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// Freshness baselines of the knowledge store (sc-27869). A knowledge entry verified against a file
/// that was not indexed yet stores the file with a NULL hash; the file's first indexed hash becomes
/// its baseline, so a later change is always a difference from it. Reads of the knowledge store
/// never write.
/// <para>
/// Adoption runs after a write commits, on its own connection and transaction, from two sides:
/// <list type="bullet">
/// <item>an index write that stored file hashes adopts them for the pending rows it can see
/// (<see cref="AdoptWrittenAsync"/>, called by MySqlGraphStore);</item>
/// <item>a knowledge write that stored pending rows adopts the hashes already committed for them
/// (<see cref="AdoptPendingAsync"/>, called by MySqlWorkStore).</item>
/// </list>
/// Each side reads only after its own commit, so whichever of the two reads later sees the other's
/// committed rows: an index run and a record that race can never both miss each other, and the row
/// is baselined with the hash of that index run. Both sides only fill a NULL hash.
/// </para>
/// <para>
/// Adoption is best effort and never fails the write that triggered it: a lock wait, deadlock,
/// timeout or missing table (an older schema) is logged and skipped; the row stays pending until the
/// next write of that file or entry adopts it. Paths match in both separator spellings and ignoring
/// case (the collation of file_hashes); the row takes file_hashes' spelling so the freshness join is exact.
/// </para>
/// </summary>
internal static class KnowledgeBaselines
{
    private const int Batch = 500;
    private const int CommandTimeoutSeconds = 5;
    private const int LockWaitSeconds = 2;

    /// <summary>After an index write committed <paramref name="hashes"/> for <paramref name="repository"/>.</summary>
    public static Task AdoptWrittenAsync(string connectionString, string repository, IReadOnlyCollection<KeyValuePair<string, string>> hashes,
        ILogger logger, CancellationToken ct) =>
        hashes.Count == 0
            ? Task.CompletedTask
            : BestEffortAsync(connectionString, logger, $"repository {repository}", async (connection, transaction) =>
            {
                var byPath = new Dictionary<string, (string RelPath, string Hash)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (relPath, hash) in hashes)
                    byPath[relPath.Replace('\\', '/')] = (relPath, hash);

                foreach (var chunk in byPath.Keys.Chunk(Batch))
                {
                    var variants = Variants(chunk);
                    var pending = (await connection.QueryAsync<(long Id, string Path)>(new CommandDefinition(
                        "SELECT id, path FROM knowledge_entry_files WHERE repository_name = @repository AND content_hash IS NULL AND path IN @variants",
                        new { repository, variants }, transaction, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
                    foreach (var (id, path) in pending)
                    {
                        if (byPath.TryGetValue(path.Replace('\\', '/'), out var written))
                            await FillAsync(connection, transaction, id, written.RelPath, written.Hash, ct);
                    }
                }
            }, ct);

    /// <summary>After a knowledge write committed <paramref name="entryIds"/>: their pending files take the hashes committed now.</summary>
    public static Task AdoptPendingAsync(string connectionString, IReadOnlyCollection<Guid> entryIds, ILogger logger, CancellationToken ct) =>
        entryIds.Count == 0
            ? Task.CompletedTask
            : BestEffortAsync(connectionString, logger, $"{entryIds.Count} knowledge entries", async (connection, transaction) =>
            {
                var pending = (await connection.QueryAsync<(long Id, string RepositoryName, string Path)>(new CommandDefinition(
                    "SELECT id, repository_name, path FROM knowledge_entry_files WHERE entry_id IN @ids AND content_hash IS NULL",
                    new { ids = entryIds.Select(id => id.ToString()).ToArray() }, transaction, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
                if (pending.Count == 0)
                    return;

                var found = (await connection.QueryAsync<(string Project, string RelPath, string ContentHash)>(new CommandDefinition(
                    "SELECT repository, rel_path, content_hash FROM file_hashes WHERE repository IN @repositories AND rel_path IN @variants",
                    new
                    {
                        repositories = pending.Select(row => row.RepositoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        variants = Variants(pending.Select(row => row.Path))
                    },
                    transaction, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
                foreach (var row in pending)
                {
                    var forward = row.Path.Replace('\\', '/');
                    var file = found.FirstOrDefault(candidate =>
                        string.Equals(candidate.Project, row.RepositoryName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.RelPath.Replace('\\', '/'), forward, StringComparison.OrdinalIgnoreCase));
                    if (file.ContentHash is not null)
                        await FillAsync(connection, transaction, row.Id, file.RelPath, file.ContentHash, ct);
                }
            }, ct);

    private static string[] Variants(IEnumerable<string> paths) =>
        paths.SelectMany(path => new[] { path.Replace('\\', '/'), path.Replace('/', '\\') }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // Fills a still-pending row only: a baseline once set is never moved.
    private static Task FillAsync(MySqlConnection connection, MySqlTransaction transaction, long id, string relPath, string hash, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE knowledge_entry_files SET path = @relPath, content_hash = @hash WHERE id = @id AND content_hash IS NULL",
            new { relPath, hash, id }, transaction, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

    private static async Task BestEffortAsync(string connectionString, ILogger logger, string what,
        Func<MySqlConnection, MySqlTransaction, Task> body, CancellationToken ct)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(ct);
            // Short waits: a baseline left pending costs nothing now, a stalled caller does.
            await connection.ExecuteAsync(new CommandDefinition(
                $"SET SESSION innodb_lock_wait_timeout = {LockWaitSeconds}", commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await body(connection, transaction);
            await transaction.CommitAsync(ct);
        }
        catch (Exception exception) when (exception is MySqlException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Knowledge freshness baselines were not adopted for {What}; they stay pending until a later write adopts them", what);
        }
    }
}
