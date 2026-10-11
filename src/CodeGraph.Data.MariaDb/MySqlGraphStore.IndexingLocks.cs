using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public partial class MySqlGraphStore
{
    private const int IndexingLockWaitSeconds = 600;

    public async Task<IAsyncDisposable> AcquireProjectIndexingLockAsync(
        string project,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = await GetOpenConnectionAsync();
        var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project)));

        try
        {
            var acquired = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                $"SELECT GET_LOCK(@LockName, {IndexingLockWaitSeconds})",
                new { LockName = lockName },
                // Outlast the server-side wait so contention reports 0 (TimeoutException below)
                // rather than MySqlConnector's 30 s default command timeout killing the wait early.
                commandTimeout: IndexingLockWaitSeconds + 30,
                cancellationToken: ct));
            if (acquired != 1)
                throw new TimeoutException($"Timed out waiting for the indexing lock for '{project}'.");

            return new MariaDbIndexingLock(connection, lockName, logger);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class MariaDbIndexingLock(
        MySqlConnection connection,
        string lockName,
        ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.ExecuteAsync(
                    "SELECT RELEASE_LOCK(@LockName)",
                    new { LockName = lockName });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to explicitly release repository indexing lock {LockName}", lockName);
            }
            finally
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispose repository indexing lock connection {LockName}", lockName);
                }
            }
        }
    }
}
