using CodeGraph.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using System.Text;

namespace CodeGraph.Data.MariaDb;

public class MariaDbMigrationRunner(
    IOptions<MariaDbStorageOptions> options,
    ILogger<MariaDbMigrationRunner> logger) : IMigrationRunner
{
    private readonly MariaDbStorageOptions options = options.Value;

    public async Task ApplyMigrationsAsync(string migrationsPath)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("MariaDB connection string is required to apply migrations.");
        }

        if (!Directory.Exists(migrationsPath))
        {
            throw new DirectoryNotFoundException($"MariaDB migrations path was not found: {migrationsPath}");
        }

        await EnsureDatabaseExistsAsync();

        await using var conn = new MySqlConnection(options.ConnectionString);
        await conn.OpenAsync();

        await EnsureMigrationHistoryAsync(conn);
        var applied = (await conn.QueryAsync<string>(
            "SELECT script_name FROM migration_history")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scripts = Directory.GetFiles(migrationsPath, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var script in scripts)
        {
            var scriptName = Path.GetFileName(script);
            if (applied.Contains(scriptName))
            {
                logger.LogDebug("Migration {Script} already applied, skipping", scriptName);
                continue;
            }

            logger.LogInformation("Applying MariaDB migration: {Script}", scriptName);
            var statements = SplitStatements(await File.ReadAllTextAsync(script));

            await using var transaction = await conn.BeginTransactionAsync();
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
                logger.LogInformation("MariaDB migration {Script} applied successfully", scriptName);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }

    public Task ApplyConfiguredMigrationsAsync()
    {
        return ApplyMigrationsAsync(options.MigrationsPath);
    }

    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new MySqlConnectionStringBuilder(options.ConnectionString);
        var dbName = builder.Database;
        if (string.IsNullOrWhiteSpace(dbName))
        {
            return;
        }

        builder.Database = "";
        await using var adminConn = new MySqlConnection(builder.ConnectionString);
        await adminConn.OpenAsync();

        var escapedDbName = dbName.Replace("`", "``", StringComparison.Ordinal);
        await adminConn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{escapedDbName}`");
    }

    private static Task EnsureMigrationHistoryAsync(MySqlConnection conn)
    {
        return conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS migration_history (
                id INT AUTO_INCREMENT PRIMARY KEY,
                script_name VARCHAR(255) NOT NULL UNIQUE,
                applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
            ) ENGINE=InnoDB
            """);
    }

    internal static IReadOnlyList<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktick = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                current.Append(c);
                if (c is '\n' or '\r')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBlockComment)
            {
                current.Append(c);
                if (c == '*' && next == '/')
                {
                    current.Append(next);
                    i++;
                    inBlockComment = false;
                }

                continue;
            }

            if (inSingleQuote)
            {
                current.Append(c);
                if (c == '\\' && next != '\0')
                {
                    current.Append(next);
                    i++;
                }
                else if (c == '\'' && next == '\'')
                {
                    current.Append(next);
                    i++;
                }
                else if (c == '\'')
                {
                    inSingleQuote = false;
                }

                continue;
            }

            if (inDoubleQuote)
            {
                current.Append(c);
                if (c == '\\' && next != '\0')
                {
                    current.Append(next);
                    i++;
                }
                else if (c == '"')
                {
                    inDoubleQuote = false;
                }

                continue;
            }

            if (inBacktick)
            {
                current.Append(c);
                if (c == '`' && next == '`')
                {
                    current.Append(next);
                    i++;
                }
                else if (c == '`')
                {
                    inBacktick = false;
                }

                continue;
            }

            if (c == '-' && next == '-')
            {
                current.Append(c);
                current.Append(next);
                i++;
                inLineComment = true;
                continue;
            }

            if (c == '#')
            {
                current.Append(c);
                inLineComment = true;
                continue;
            }

            if (c == '/' && next == '*')
            {
                current.Append(c);
                current.Append(next);
                i++;
                inBlockComment = true;
                continue;
            }

            if (c == '\'')
            {
                current.Append(c);
                inSingleQuote = true;
                continue;
            }

            if (c == '"')
            {
                current.Append(c);
                inDoubleQuote = true;
                continue;
            }

            if (c == '`')
            {
                current.Append(c);
                inBacktick = true;
                continue;
            }

            if (c == ';')
            {
                AddStatement(statements, current);
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        AddStatement(statements, current);
        return statements;
    }

    private static void AddStatement(List<string> statements, StringBuilder builder)
    {
        var statement = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(statement))
        {
            statements.Add(statement);
        }
    }
}
