using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public partial class MySqlGraphStore
{
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
                "SELECT GET_LOCK(@LockName, 600)",
                new { LockName = lockName },
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
