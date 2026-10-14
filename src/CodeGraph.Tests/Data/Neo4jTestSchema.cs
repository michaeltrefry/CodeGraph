using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace CodeGraph.Tests.Data;

/// <summary>
/// Brings the Neo4j test database to the schema every production host applies at startup
/// (<c>src/CodeGraph.Api/Migrations/*.cypher</c>) before any test touches it. The graph store
/// allocates <c>appId</c>s with <c>MERGE (:Sequence {name})</c>, which is only atomic under the
/// <c>sequence_name</c> uniqueness constraint; on a fresh, unmigrated database parallel test
/// classes each create their own <c>node_id</c> sequence and hand out colliding ids.
/// </summary>
internal static class Neo4jTestSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool applied;

    public static async Task EnsureAppliedAsync(IOptions<CodeGraphStorageOptions> options)
    {
        if (Volatile.Read(ref applied)) return;
        await Gate.WaitAsync();
        try
        {
            if (applied) return;
            var migrationsPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "../../../../CodeGraph.Api/Migrations"));
            if (!Directory.Exists(migrationsPath))
                throw new DirectoryNotFoundException($"Neo4j migrations not found at {migrationsPath}.");

            await using var sessions = new Neo4jSessionFactory(options);
            await new Neo4jGraphStore(sessions, options, NullLogger<Neo4jGraphStore>.Instance)
                .ApplyMigrationsAsync(migrationsPath);

            // ApplyMigrationsAsync logs and skips failed statements; fail loudly instead of
            // letting tests race on an unconstrained sequence.
            await using var session = sessions.GetSession();
            var hasSequenceConstraint = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "SHOW CONSTRAINTS YIELD name WHERE name = 'sequence_name' RETURN count(*) AS c");
                return (await cursor.SingleAsync())["c"].As<long>() == 1;
            });
            if (!hasSequenceConstraint)
                throw new InvalidOperationException("Neo4j migrations did not create the sequence_name constraint.");

            Volatile.Write(ref applied, true);
        }
        finally
        {
            Gate.Release();
        }
    }
}
