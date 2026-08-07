using CodeGraph.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Data.MariaDb;

public class MariaDbMigrationRunner(
    IOptions<MariaDbStorageOptions> options,
    ILogger<MariaDbMigrationRunner> logger) : IMigrationRunner
{
    private const string StatementStatusStarted = "started";
    private const string StatementStatusCompleted = "completed";
    private const string StatementStatusFailed = "failed";
    private readonly MariaDbStorageOptions options = options.Value;

    internal Func<string, int, Task>? StatementExecutedHook { get; set; }

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

        var lockName = BuildLockName(conn.Database);
        var lockAcquired = false;
        try
        {
            lockAcquired = await AcquireMigrationLockAsync(conn, lockName);
            if (!lockAcquired)
            {
                throw new TimeoutException(
                    $"Timed out after {options.MigrationLockTimeoutSeconds} seconds waiting for MariaDB migration lock '{lockName}'.");
            }

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
                var temporaryTableNames = GetTemporaryTableNames(statements);

                for (var statementIndex = 0; statementIndex < statements.Count; statementIndex++)
                {
                    var statement = statements[statementIndex];
                    await ApplyStatementAsync(
                        conn,
                        scriptName,
                        statementIndex + 1,
                        statement,
                        IsSessionSetupStatement(statement, temporaryTableNames));
                }

                await conn.ExecuteAsync(
                    "INSERT INTO migration_history (script_name) VALUES (@ScriptName)",
                    new { ScriptName = scriptName });
                applied.Add(scriptName);
                logger.LogInformation("MariaDB migration {Script} applied successfully", scriptName);
            }
        }
        finally
        {
            if (lockAcquired && conn.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    await conn.ExecuteScalarAsync<int?>("SELECT RELEASE_LOCK(@LockName)", new { LockName = lockName });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to explicitly release MariaDB migration lock {LockName}", lockName);
                }
            }
        }
    }

    public Task ApplyConfiguredMigrationsAsync()
    {
        return ApplyMigrationsAsync(options.MigrationsPath);
    }

    private async Task ApplyStatementAsync(
        MySqlConnection conn,
        string scriptName,
        int statementOrdinal,
        string statement,
        bool sessionSetupStatement)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(statement))).ToLowerInvariant();
        var previous = await conn.QuerySingleOrDefaultAsync<StatementJournalEntry>(
            """
            SELECT statement_checksum AS StatementChecksum, status AS Status
            FROM migration_statement_history
            WHERE script_name = @ScriptName AND statement_ordinal = @StatementOrdinal
            """,
            new { ScriptName = scriptName, StatementOrdinal = statementOrdinal });

        if (previous is not null && !string.Equals(previous.StatementChecksum, checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MariaDB migration {scriptName} statement {statementOrdinal} changed after execution began. " +
                $"Recorded checksum {previous.StatementChecksum}; current checksum {checksum}.");
        }

        if (string.Equals(previous?.Status, StatementStatusCompleted, StringComparison.OrdinalIgnoreCase) &&
            !sessionSetupStatement)
        {
            logger.LogDebug(
                "MariaDB migration {Script} statement {StatementOrdinal} already completed, skipping",
                scriptName,
                statementOrdinal);
            return;
        }

        if (sessionSetupStatement &&
            string.Equals(previous?.Status, StatementStatusCompleted, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Replaying session-scoped setup for MariaDB migration {Script} statement {StatementOrdinal}",
                scriptName,
                statementOrdinal);
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO migration_statement_history (
                script_name,
                statement_ordinal,
                statement_checksum,
                status,
                attempt_count,
                last_started_at,
                completed_at,
                last_error)
            VALUES (
                @ScriptName,
                @StatementOrdinal,
                @Checksum,
                @Status,
                1,
                CURRENT_TIMESTAMP(3),
                NULL,
                NULL)
            ON DUPLICATE KEY UPDATE
                status = VALUES(status),
                attempt_count = attempt_count + 1,
                last_started_at = VALUES(last_started_at),
                completed_at = NULL,
                last_error = NULL
            """,
            new
            {
                ScriptName = scriptName,
                StatementOrdinal = statementOrdinal,
                Checksum = checksum,
                Status = StatementStatusStarted
            });

        try
        {
            await conn.ExecuteAsync(statement);
        }
        catch (MySqlException ex)
        {
            try
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE migration_statement_history
                    SET status = @Status,
                        last_error = @Error
                    WHERE script_name = @ScriptName AND statement_ordinal = @StatementOrdinal
                    """,
                    new
                    {
                        ScriptName = scriptName,
                        StatementOrdinal = statementOrdinal,
                        Status = StatementStatusFailed,
                        Error = ex.Message.Length <= 4000 ? ex.Message : ex.Message[..4000]
                    });
            }
            catch (Exception journalException)
            {
                logger.LogError(
                    journalException,
                    "Failed to journal MariaDB migration error for {Script} statement {StatementOrdinal}",
                    scriptName,
                    statementOrdinal);
            }

            throw;
        }

        if (StatementExecutedHook is not null)
        {
            await StatementExecutedHook(scriptName, statementOrdinal);
        }

        await conn.ExecuteAsync(
            """
            UPDATE migration_statement_history
            SET status = @Status,
                completed_at = CURRENT_TIMESTAMP(3),
                last_error = NULL
            WHERE script_name = @ScriptName AND statement_ordinal = @StatementOrdinal
            """,
            new
            {
                ScriptName = scriptName,
                StatementOrdinal = statementOrdinal,
                Status = StatementStatusCompleted
            });

        logger.LogDebug(
            "MariaDB migration {Script} statement {StatementOrdinal} completed",
            scriptName,
            statementOrdinal);
    }

    private async Task<bool> AcquireMigrationLockAsync(MySqlConnection conn, string lockName)
    {
        logger.LogInformation("Waiting for MariaDB migration lock {LockName}", lockName);
        var result = await conn.ExecuteScalarAsync<int?>(
            "SELECT GET_LOCK(@LockName, @TimeoutSeconds)",
            new
            {
                LockName = lockName,
                TimeoutSeconds = Math.Max(0, options.MigrationLockTimeoutSeconds)
            });
        return result == 1;
    }

    internal static string BuildLockName(string databaseName)
    {
        var normalizedDatabaseName = databaseName.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDatabaseName)))
            .ToLowerInvariant();
        return $"codegraph:migrations:{hash[..32]}";
    }

    internal static IReadOnlySet<string> GetTemporaryTableNames(IEnumerable<string> statements)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in statements)
        {
            foreach (Match match in Regex.Matches(
                statement,
                @"\bCREATE\s+TEMPORARY\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?`?(?<name>[A-Za-z0-9_]+)`?",
                RegexOptions.IgnoreCase))
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    internal static bool IsSessionSetupStatement(
        string statement,
        IReadOnlySet<string> temporaryTableNames)
    {
        return temporaryTableNames.Any(tableName =>
            Regex.IsMatch(
                statement,
                $@"(?<![A-Za-z0-9_])`?{Regex.Escape(tableName)}`?(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase));
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

    private static async Task EnsureMigrationHistoryAsync(MySqlConnection conn)
    {
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS migration_history (
                id INT AUTO_INCREMENT PRIMARY KEY,
                script_name VARCHAR(255) NOT NULL UNIQUE,
                applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
            ) ENGINE=InnoDB
            """);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS migration_statement_history (
                script_name VARCHAR(255) NOT NULL,
                statement_ordinal INT NOT NULL,
                statement_checksum CHAR(64) NOT NULL,
                status VARCHAR(32) NOT NULL,
                attempt_count INT NOT NULL DEFAULT 0,
                last_started_at DATETIME(3) NOT NULL,
                completed_at DATETIME(3) NULL,
                last_error TEXT NULL,
                PRIMARY KEY (script_name, statement_ordinal),
                INDEX ix_migration_statement_history_status (status)
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

    private sealed record StatementJournalEntry(string StatementChecksum, string Status);
}
