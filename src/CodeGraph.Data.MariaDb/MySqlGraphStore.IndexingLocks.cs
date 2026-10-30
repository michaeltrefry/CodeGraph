using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Data.MariaDb;

public partial class MySqlGraphStore
{
    private const int IndexingLockWaitSeconds = 600;

    public Task<IAsyncDisposable> AcquireRepositoryIndexingLockAsync(
        string repository, CancellationToken ct = default) =>
        AcquireRepositoryIndexingLocksAsync([repository], ct);

    public Task<IAsyncDisposable> AcquireRepositoryIndexingLocksAsync(
        IReadOnlyCollection<string> repositories, CancellationToken ct = default) =>
        AcquireIndexingLocksAsync(repositories, async token => await GetOpenConnectionAsync(token), logger, ct);

    // The injected connection factory keeps the actual SQL/cleanup path testable without a server.
    internal static async Task<IAsyncDisposable> AcquireIndexingLocksAsync(
        IReadOnlyCollection<string> repositories, Func<CancellationToken, Task<DbConnection>> openConnection,
        ILogger logger, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var names = repositories.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var attempted = new List<string>();
        var connection = names.Count == 0 ? null : await openConnection(ct);
        var lease = new MariaDbIndexingLock(connection, attempted, logger);
        try
        {
            foreach (var repository in names)
            {
                ct.ThrowIfCancellationRequested();
                // Keep exact identity interoperable with every existing single-repository caller.
                var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository)));
                // A canceled response may have acquired the lock on the server. Releasing an
                // attempted name on this same connection is safe even if acquisition failed.
                attempted.Add(lockName);
                var acquired = await connection!.ExecuteScalarAsync<long?>(new CommandDefinition(
                    $"SELECT GET_LOCK(@LockName, {IndexingLockWaitSeconds})", new { LockName = lockName },
                    commandTimeout: IndexingLockWaitSeconds + 30, cancellationToken: ct));
                if (acquired != 1)
                    throw new TimeoutException($"Timed out waiting for the indexing lock for '{repository}'.");
            }
            ct.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private sealed class MariaDbIndexingLock(
        DbConnection? connection, IReadOnlyList<string> lockNames, ILogger logger) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || connection is null) return;
            try
            {
                // Cleanup must run even when the caller's token was canceled.
                foreach (var lockName in lockNames.Reverse())
                {
                    try
                    {
                        await connection.ExecuteAsync(new CommandDefinition(
                            "SELECT RELEASE_LOCK(@LockName)", new { LockName = lockName },
                            cancellationToken: CancellationToken.None));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to explicitly release repository indexing lock {LockName}", lockName);
                    }
                }
            }
            finally
            {
                try { await connection.DisposeAsync(); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to dispose repository indexing lock connection"); }
            }
        }
    }
}
