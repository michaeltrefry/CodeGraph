using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace CodeGraph.Data;

public partial class MySqlGraphStore
{
    // ── Migrations (Dapper — raw SQL execution) ───────────────────────────

    public async Task ApplyMigrationsAsync(string migrationsPath)
    {
        // Ensure the database exists before connecting to it
        var builder = new MySqlConnectionStringBuilder(options.ConnectionString);
        var dbName = builder.Database;
        if (!string.IsNullOrEmpty(dbName))
        {
            builder.Database = "";
            using var adminConn = new MySqlConnection(builder.ConnectionString);
            await adminConn.OpenAsync();
            await adminConn.ExecuteAsync(
                $"CREATE DATABASE IF NOT EXISTS `{dbName}`");
        }

        await using var conn = await GetOpenConnectionAsync();

        // Ensure migration_history table exists
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS migration_history (
                id INT AUTO_INCREMENT PRIMARY KEY,
                script_name VARCHAR(255) NOT NULL UNIQUE,
                applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
            ) ENGINE=InnoDB
            """);

        var applied = (await conn.QueryAsync<string>(
            "SELECT script_name FROM migration_history")).ToHashSet();

        var scripts = Directory.GetFiles(migrationsPath, "*.sql")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        foreach (var script in scripts)
        {
            var scriptName = Path.GetFileName(script);
            if (applied.Contains(scriptName))
            {
                logger.LogDebug("Migration {Script} already applied, skipping", scriptName);
                continue;
            }

            logger.LogInformation("Applying migration: {Script}", scriptName);
            var sql = await File.ReadAllTextAsync(script);

            // Split on semicolons for multi-statement scripts
            var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            using var transaction = await conn.BeginTransactionAsync();
            try
            {
                foreach (var statement in statements)
                {
                    await conn.ExecuteAsync(statement, transaction: transaction);
                }

                await conn.ExecuteAsync(
                    "INSERT INTO migration_history (script_name) VALUES (@ScriptName)",
                    new { ScriptName = scriptName },
                    transaction: transaction);

                await transaction.CommitAsync();
                logger.LogInformation("Migration {Script} applied successfully", scriptName);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }
}
