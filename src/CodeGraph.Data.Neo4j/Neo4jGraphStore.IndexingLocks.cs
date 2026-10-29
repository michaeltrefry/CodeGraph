using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    private const string RepositoryIndexLockConstraint = "repository_index_lock_unique";

    public async Task<IAsyncDisposable> AcquireRepositoryIndexingLockAsync(
        string repository,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var session = sessionFactory.GetSession();
        try
        {
            var constraintCursor = await session.RunAsync("""
                SHOW CONSTRAINTS YIELD name, type, entityType, labelsOrTypes, properties
                WHERE name = $constraintName
                  AND type IN ['UNIQUENESS', 'NODE_PROPERTY_UNIQUENESS']
                  AND entityType = 'NODE'
                  AND labelsOrTypes = ['RepositoryIndexLock']
                  AND properties = ['repository']
                RETURN count(*) AS constraintCount
                """, new { constraintName = RepositoryIndexLockConstraint });
            if (!await constraintCursor.FetchAsync() ||
                constraintCursor.Current["constraintCount"].As<long>() != 1)
            {
                throw new InvalidOperationException(
                    $"Neo4j constraint '{RepositoryIndexLockConstraint}' is required for repository indexing locks.");
            }

            var transaction = await session.BeginTransactionAsync(config =>
                config.WithTimeout(TimeSpan.FromHours(12)));
            try
            {
                var cursor = await transaction.RunAsync("""
                    MERGE (l:RepositoryIndexLock {repository: $repository})
                    SET l.owner = $owner
                    RETURN l.owner
                    """, new { repository, owner = Guid.NewGuid().ToString("N") });
                await cursor.ConsumeAsync();
                return new Neo4jIndexingLock(session, transaction, logger, repository);
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private sealed class Neo4jIndexingLock(
        IAsyncSession session,
        IAsyncTransaction transaction,
        MsLogger logger,
        string repository) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to roll back repository indexing lock for {Repository}", repository);
            }

            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose repository indexing lock transaction for {Repository}", repository);
            }

            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose repository indexing lock session for {Repository}", repository);
            }
        }
    }
}
