using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    private const string ProjectIndexLockConstraint = "project_index_lock_unique";

    public async Task<IAsyncDisposable> AcquireProjectIndexingLockAsync(
        string project,
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
                  AND labelsOrTypes = ['ProjectIndexLock']
                  AND properties = ['project']
                RETURN count(*) AS constraintCount
                """, new { constraintName = ProjectIndexLockConstraint });
            if (!await constraintCursor.FetchAsync() ||
                constraintCursor.Current["constraintCount"].As<long>() != 1)
            {
                throw new InvalidOperationException(
                    $"Neo4j constraint '{ProjectIndexLockConstraint}' is required for repository indexing locks.");
            }

            var transaction = await session.BeginTransactionAsync(config =>
                config.WithTimeout(TimeSpan.FromHours(12)));
            try
            {
                var cursor = await transaction.RunAsync("""
                    MERGE (l:ProjectIndexLock {project: $project})
                    SET l.owner = $owner
                    RETURN l.owner
                    """, new { project, owner = Guid.NewGuid().ToString("N") });
                await cursor.ConsumeAsync();
                return new Neo4jIndexingLock(session, transaction, logger, project);
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
        string project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to roll back repository indexing lock for {Project}", project);
            }

            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose repository indexing lock transaction for {Project}", project);
            }

            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose repository indexing lock session for {Project}", project);
            }
        }
    }
}
