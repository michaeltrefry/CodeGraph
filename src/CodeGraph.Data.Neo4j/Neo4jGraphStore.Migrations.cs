using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Migrations (Cypher scripts) ──────────────────────────────────────

    internal static IReadOnlyList<string> ParseMigrationStatements(string cypher)
    {
        var uncommented = string.Join(
            Environment.NewLine,
            cypher.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        return uncommented
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => !string.IsNullOrWhiteSpace(statement))
            .ToList();
    }

    public async Task ApplyMigrationsAsync(string migrationsPath)
    {
        var cypherPath = string.IsNullOrWhiteSpace(migrationsPath)
            ? options.Neo4jMigrationsPath
            : migrationsPath;
        if (!Directory.Exists(cypherPath))
        {
            logger.LogWarning("Neo4j migrations path {Path} does not exist, skipping", cypherPath);
            return;
        }

        await using var session = sessionFactory.GetSession();

        // Ensure MigrationHistory constraint exists
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                CREATE CONSTRAINT migration_history_script IF NOT EXISTS
                FOR (m:MigrationHistory) REQUIRE m.scriptName IS UNIQUE
                """);
        });

        // Get applied migrations
        var applied = new HashSet<string>();
        await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (m:MigrationHistory) RETURN m.scriptName AS name");
            await foreach (var record in cursor)
                applied.Add(record["name"].As<string>());
            return applied;
        });

        var scripts = Directory.GetFiles(cypherPath, "*.cypher")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        foreach (var script in scripts)
        {
            var scriptName = Path.GetFileName(script);
            if (applied.Contains(scriptName))
            {
                logger.LogDebug("Neo4j migration {Script} already applied, skipping", scriptName);
                continue;
            }

            logger.LogInformation("Applying Neo4j migration: {Script}", scriptName);
            var cypher = await File.ReadAllTextAsync(script);

            // Strip comment-only lines before splitting so section headers do not
            // accidentally suppress the first real statement in a block.
            var statements = ParseMigrationStatements(cypher);

            // Neo4j cannot mix schema operations (CREATE CONSTRAINT/INDEX) with
            // data writes in the same transaction. Run each statement separately.
            var failedStatements = new List<(string Statement, string Error)>();
            foreach (var statement in statements)
            {
                try
                {
                    await session.ExecuteWriteAsync(async tx =>
                    {
                        await tx.RunAsync(statement);
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Neo4j migration {Script} — statement failed: {Statement}",
                        scriptName, statement.Length > 200 ? statement[..200] + "..." : statement);
                    failedStatements.Add((statement, ex.Message));
                }
            }

            if (failedStatements.Count > 0)
            {
                logger.LogError(
                    "Neo4j migration {Script} had {FailedCount}/{TotalCount} failed statements. " +
                    "Migration will NOT be marked as applied — fix the errors and re-run.",
                    scriptName, failedStatements.Count, statements.Count);
                continue;
            }

            // Record migration only if all statements succeeded
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    "CREATE (m:MigrationHistory {scriptName: $scriptName, appliedAt: datetime()}) RETURN m",
                    new { scriptName });
            });

            logger.LogInformation("Neo4j migration {Script} applied successfully ({Count} statements)",
                scriptName, statements.Count);
        }
    }
}
