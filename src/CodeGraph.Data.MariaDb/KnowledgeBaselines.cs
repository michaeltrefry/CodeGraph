using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// Freshness baselines of the knowledge store (sc-27869), adopted at index time. A knowledge entry
/// verified against a file that was not indexed yet stores that file with a NULL hash; the index
/// write that first stores the file's hash also stores it as the entry's baseline, in the same
/// transaction, so a later change is always a difference from it. Reads of the knowledge store
/// never write. Paths are matched in both separator spellings, as the slice writes match them.
/// </summary>
internal static class KnowledgeBaselines
{
    private const int Batch = 500;

    public static async Task AdoptAsync(MySqlConnection connection, MySqlTransaction? transaction, string repository,
        IReadOnlyCollection<KeyValuePair<string, string>> hashes, int? commandTimeout, CancellationToken ct)
    {
        if (hashes.Count == 0)
            return;

        // The written hash of each file, by its forward-slash spelling (the knowledge store's).
        var byPath = new Dictionary<string, (string RelPath, string Hash)>(StringComparer.Ordinal);
        foreach (var (relPath, hash) in hashes)
            byPath[relPath.Replace('\\', '/')] = (relPath, hash);

        foreach (var chunk in byPath.Keys.Chunk(Batch))
        {
            var variants = chunk.SelectMany(path => new[] { path, path.Replace('/', '\\') }).Distinct(StringComparer.Ordinal).ToArray();
            var pending = (await connection.QueryAsync<(long Id, string Path)>(new CommandDefinition(
                "SELECT id, path FROM knowledge_entry_files WHERE repository_name = @repository AND content_hash IS NULL AND path IN @variants",
                new { repository, variants }, transaction, commandTimeout: commandTimeout, cancellationToken: ct))).ToList();
            foreach (var (id, path) in pending)
            {
                if (!byPath.TryGetValue(path.Replace('\\', '/'), out var written))
                    continue;
                // The stored spelling becomes the file's key, so the freshness join is exact.
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE knowledge_entry_files SET path = @path, content_hash = @hash WHERE id = @id",
                    new { path = written.RelPath, hash = written.Hash, id }, transaction, commandTimeout: commandTimeout, cancellationToken: ct));
            }
        }
    }
}
