using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;
using System.Text.RegularExpressions;

namespace CodeGraph.Tests.Data;

public class MariaDbMigrationRunnerTests
{
    private static readonly string[] MigrationHostNames =
    [
        "api",
        "indexer",
        "memory",
        "metrics",
        "jobs"
    ];

    [Fact]
    public void SplitStatements_OrdersNonEmptyStatementsFromScript()
    {
        const string sql = """
            CREATE TABLE repositories (
                name VARCHAR(255) NOT NULL
            );

            ALTER TABLE repositories
                ADD COLUMN repo_url TEXT NULL;
            """;

        var statements = MariaDbMigrationRunner.SplitStatements(sql);

        statements.Count.ShouldBe(2);
        statements[0].ShouldStartWith("CREATE TABLE repositories");
        statements[1].ShouldStartWith("ALTER TABLE repositories");
    }

    [Fact]
    public void SplitStatements_DoesNotSplitSemicolonsInsideLiteralsOrComments()
    {
        const string sql = """
            -- Comment with a ; semicolon
            INSERT INTO wiki_pages (content)
            VALUES ('A body with a ; semicolon and escaped '' quote');

            /* Block comment with ; semicolon */
            UPDATE `odd;table`
            SET value = "double ; quoted";
            """;

        var statements = MariaDbMigrationRunner.SplitStatements(sql);

        statements.Count.ShouldBe(2);
        statements[0].ShouldContain("A body with a ; semicolon");
        statements[1].ShouldContain("double ; quoted");
    }

    [Fact]
    public void BuildLockName_IsDatabaseScopedAndWithinMariaDbIdentifierLimit()
    {
        var first = MariaDbMigrationRunner.BuildLockName("CodeGraph");
        var sameDatabase = MariaDbMigrationRunner.BuildLockName(" codegraph ");
        var otherDatabase = MariaDbMigrationRunner.BuildLockName("codegraph_test");

        first.ShouldBe(sameDatabase);
        first.ShouldNotBe(otherDatabase);
        first.Length.ShouldBeLessThanOrEqualTo(64);
    }

    [Fact]
    public void HistoricalMigrations_HaveRestartSafePersistentDdlAndInserts()
    {
        var migrationsPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var unsafePatterns = new Dictionary<string, Regex>
        {
            ["CREATE TABLE without IF NOT EXISTS"] = new(
                @"\bCREATE\s+TABLE\s+(?!IF\s+NOT\s+EXISTS)",
                RegexOptions.IgnoreCase),
            ["CREATE INDEX without IF NOT EXISTS"] = new(
                @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?!IF\s+NOT\s+EXISTS)",
                RegexOptions.IgnoreCase),
            ["ADD COLUMN without IF NOT EXISTS"] = new(
                @"\bADD\s+COLUMN\s+(?!IF\s+NOT\s+EXISTS)",
                RegexOptions.IgnoreCase),
            ["ADD UNIQUE KEY without IF NOT EXISTS"] = new(
                @"\bADD\s+UNIQUE\s+KEY\s+(?!IF\s+NOT\s+EXISTS)",
                RegexOptions.IgnoreCase),
            ["ADD INDEX without IF NOT EXISTS"] = new(
                @"\bADD\s+INDEX\s+(?!IF\s+NOT\s+EXISTS)",
                RegexOptions.IgnoreCase),
            ["DROP INDEX without IF EXISTS"] = new(
                @"\bDROP\s+INDEX\s+(?!IF\s+EXISTS)",
                RegexOptions.IgnoreCase),
            ["DROP TABLE without IF EXISTS"] = new(
                @"\bDROP\s+(?:TEMPORARY\s+)?TABLE\s+(?!IF\s+EXISTS)",
                RegexOptions.IgnoreCase),
            ["TRUNCATE is not restart safe"] = new(
                @"\bTRUNCATE\s+TABLE\b",
                RegexOptions.IgnoreCase),
            ["INSERT without duplicate recovery or an existence precondition"] = new(
                @"\bINSERT\s+INTO\b(?![\s\S]*(?:\bON\s+DUPLICATE\s+KEY\s+UPDATE\b|\bNOT\s+EXISTS\b|\bexisting\.id\s+IS\s+NULL\b))",
                RegexOptions.IgnoreCase)
        };

        var failures = new List<string>();
        foreach (var migrationFile in Directory.EnumerateFiles(migrationsPath, "*.sql"))
        {
            var statements = MariaDbMigrationRunner.SplitStatements(File.ReadAllText(migrationFile));
            for (var statementIndex = 0; statementIndex < statements.Count; statementIndex++)
            {
                foreach (var (description, pattern) in unsafePatterns)
                {
                    if (pattern.IsMatch(statements[statementIndex]))
                    {
                        failures.Add($"{Path.GetFileName(migrationFile)} statement {statementIndex + 1}: {description}");
                    }
                }
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void HistoricalMigrations_HaveAnExplicitDataMutationInventory()
    {
        var migrationsPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var expectedMutationCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["010_wiki_raw_content.sql"] = 1,
            ["013_fix_null_dotnet_project.sql"] = 4,
            ["033_memory_entity_canonical_id.sql"] = 15,
            ["042_metric_event_ids.sql"] = 2,
            ["048_standalone_memory_external_ids.sql"] = 3,
            ["051_embedding_provenance_cutover.sql"] = 1,
            ["058_retire_shortcut_shim.sql"] = 2
        };
        var mutationPattern = new Regex(
            @"(?:^|\n)\s*(?:UPDATE\b|DELETE\b|TRUNCATE\s+TABLE\b)",
            RegexOptions.IgnoreCase);

        var actualMutationCounts = Directory.EnumerateFiles(migrationsPath, "*.sql")
            .Select(file => new
            {
                FileName = Path.GetFileName(file),
                Count = MariaDbMigrationRunner.SplitStatements(File.ReadAllText(file))
                    .Count(statement => mutationPattern.IsMatch(statement))
            })
            .Where(item => item.Count > 0)
            .ToDictionary(item => item.FileName, item => item.Count, StringComparer.OrdinalIgnoreCase);

        actualMutationCounts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ShouldBe(expectedMutationCounts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApplyMigrationsAsync_ConcurrentHostStartupAppliesEachStatementExactlyOnce()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_migration_concurrency_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var migrationsPath = CreateMigrationDirectory(
            "001_concurrent.sql",
            """
            CREATE TABLE IF NOT EXISTS concurrency_probe (
                id INT NOT NULL PRIMARY KEY
            );

            INSERT INTO concurrency_probe (id) VALUES (1)
            ON DUPLICATE KEY UPDATE id = VALUES(id);
            """);

        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostStarts = MigrationHostNames.Select(async _ =>
            {
                var runner = CreateRunner(builder.ConnectionString, migrationsPath);
                await start.Task;
                await runner.ApplyConfiguredMigrationsAsync();
            }).ToArray();

            start.SetResult();
            await Task.WhenAll(hostStarts);

            await using var conn = new MySqlConnection(builder.ConnectionString);
            var scriptCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM migration_history WHERE script_name = '001_concurrent.sql'");
            var statementCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM migration_statement_history WHERE script_name = '001_concurrent.sql'");
            var totalAttempts = await conn.ExecuteScalarAsync<int>(
                "SELECT SUM(attempt_count) FROM migration_statement_history WHERE script_name = '001_concurrent.sql'");
            var probeCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM concurrency_probe");

            scriptCount.ShouldBe(1);
            statementCount.ShouldBe(2);
            totalAttempts.ShouldBe(2);
            probeCount.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(migrationsPath, recursive: true);
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ApplyMigrationsAsync_FailureAfterEveryStatementRetriesRestartSafeHistoryToCompletion()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_migration_restart_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var migrationsPath = CreateMigrationDirectory(
            "001_restart.sql",
            """
            CREATE TABLE IF NOT EXISTS restart_probe (
                id INT NOT NULL PRIMARY KEY
            );

            ALTER TABLE restart_probe
                ADD COLUMN IF NOT EXISTS payload VARCHAR(64) NULL;

            INSERT INTO restart_probe (id, payload) VALUES (1, 'once')
            ON DUPLICATE KEY UPDATE payload = VALUES(payload);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_restart_probe_payload ON restart_probe (payload);
            """);
        var injectedFailures = new HashSet<int>();

        try
        {
            var runner = CreateRunner(builder.ConnectionString, migrationsPath);
            runner.StatementExecutedHook = (_, statementOrdinal) =>
            {
                if (injectedFailures.Add(statementOrdinal))
                {
                    throw new InjectedMigrationFailureException(statementOrdinal);
                }

                return Task.CompletedTask;
            };

            var completed = false;
            for (var attempt = 0; attempt < 6 && !completed; attempt++)
            {
                try
                {
                    await runner.ApplyConfiguredMigrationsAsync();
                    completed = true;
                }
                catch (InjectedMigrationFailureException)
                {
                    // Simulates the host disappearing after MariaDB committed the statement
                    // but before the statement journal could be marked complete.
                }
            }

            completed.ShouldBeTrue();
            injectedFailures.OrderBy(value => value).ShouldBe([1, 2, 3, 4]);

            await using var conn = new MySqlConnection(builder.ConnectionString);
            var completedStatements = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM migration_statement_history
                WHERE script_name = '001_restart.sql'
                  AND status = 'completed'
                """);
            var totalAttempts = await conn.ExecuteScalarAsync<int>("""
                SELECT SUM(attempt_count)
                FROM migration_statement_history
                WHERE script_name = '001_restart.sql'
                """);
            var rowCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM restart_probe");
            var payload = await conn.ExecuteScalarAsync<string>("SELECT payload FROM restart_probe WHERE id = 1");
            var indexCount = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM information_schema.statistics
                WHERE table_schema = DATABASE()
                  AND table_name = 'restart_probe'
                  AND index_name = 'ux_restart_probe_payload'
                """);

            completedStatements.ShouldBe(4);
            totalAttempts.ShouldBe(8);
            rowCount.ShouldBe(1);
            payload.ShouldBe("once");
            indexCount.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(migrationsPath, recursive: true);
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ApplyMigrationsAsync_DoesNotTreatDuplicateErrorsAsProofOfCompletion()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"cg_mig_ambiguous_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var migrationsPath = CreateMigrationDirectory(
            "001_ambiguous.sql",
            "CREATE TABLE ambiguous_probe (id INT NOT NULL PRIMARY KEY);");

        try
        {
            var runner = CreateRunner(builder.ConnectionString, migrationsPath);
            runner.StatementExecutedHook = (_, statementOrdinal) =>
                throw new InjectedMigrationFailureException(statementOrdinal);

            await Should.ThrowAsync<InjectedMigrationFailureException>(
                runner.ApplyConfiguredMigrationsAsync());

            runner.StatementExecutedHook = null;
            var retryException = await Should.ThrowAsync<MySqlException>(
                runner.ApplyConfiguredMigrationsAsync());

            retryException.Number.ShouldBe(1050);
            await using var conn = new MySqlConnection(builder.ConnectionString);
            var journalStatus = await conn.ExecuteScalarAsync<string>("""
                SELECT status
                FROM migration_statement_history
                WHERE script_name = '001_ambiguous.sql' AND statement_ordinal = 1
                """);
            journalStatus.ShouldBe("failed");
        }
        finally
        {
            Directory.Delete(migrationsPath, recursive: true);
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ApplyMigrationsAsync_ReplaysTemporaryTableDependencyChainAfterReconnect()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"cg_mig_temp_replay_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var migrationsPath = CreateMigrationDirectory(
            "001_temp_replay.sql",
            """
            CREATE TABLE IF NOT EXISTS temp_replay_probe (
                id INT NOT NULL PRIMARY KEY,
                payload VARCHAR(64) NOT NULL
            );

            INSERT INTO temp_replay_probe (id, payload) VALUES (1, 'before')
            ON DUPLICATE KEY UPDATE payload = VALUES(payload);

            CREATE TEMPORARY TABLE tmp_replay_values AS
            SELECT id, 'after' AS payload FROM temp_replay_probe;

            UPDATE temp_replay_probe target
            JOIN tmp_replay_values source ON source.id = target.id
            SET target.payload = source.payload;

            DROP TEMPORARY TABLE IF EXISTS tmp_replay_values;
            """);
        var injectedFailures = new HashSet<int>();

        try
        {
            var runner = CreateRunner(builder.ConnectionString, migrationsPath);
            runner.StatementExecutedHook = (_, statementOrdinal) =>
            {
                if (injectedFailures.Add(statementOrdinal))
                {
                    throw new InjectedMigrationFailureException(statementOrdinal);
                }

                return Task.CompletedTask;
            };

            var completed = false;
            for (var attempt = 0; attempt < 8 && !completed; attempt++)
            {
                try
                {
                    await runner.ApplyConfiguredMigrationsAsync();
                    completed = true;
                }
                catch (InjectedMigrationFailureException)
                {
                    // The next attempt uses a new connection and therefore a new session.
                }
            }

            completed.ShouldBeTrue();
            injectedFailures.OrderBy(value => value).ShouldBe([1, 2, 3, 4, 5]);
            await using var conn = new MySqlConnection(builder.ConnectionString);
            var payload = await conn.ExecuteScalarAsync<string>(
                "SELECT payload FROM temp_replay_probe WHERE id = 1");
            payload.ShouldBe("after");
        }
        finally
        {
            Directory.Delete(migrationsPath, recursive: true);
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ApplyMigrationsAsync_FailureAfterEveryImportedStatementRetriesFullHistoryToCompletion()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

        var migrationsPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var expectedStatementCount = Directory.EnumerateFiles(migrationsPath, "*.sql")
            .Sum(file => MariaDbMigrationRunner.SplitStatements(File.ReadAllText(file)).Count);
        var expectedScriptCount = Directory.EnumerateFiles(migrationsPath, "*.sql").Count();
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"cg_mig_all_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var injectedFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var runner = CreateRunner(builder.ConnectionString, migrationsPath);
            runner.StatementExecutedHook = (scriptName, statementOrdinal) =>
            {
                if (injectedFailures.Add($"{scriptName}:{statementOrdinal}"))
                {
                    throw new InjectedMigrationFailureException(statementOrdinal);
                }

                return Task.CompletedTask;
            };

            var completed = false;
            for (var attempt = 0; attempt < expectedStatementCount + 2 && !completed; attempt++)
            {
                try
                {
                    await runner.ApplyConfiguredMigrationsAsync();
                    completed = true;
                }
                catch (InjectedMigrationFailureException)
                {
                    // Continue from the per-statement journal on a new MariaDB connection.
                }
            }

            completed.ShouldBeTrue();
            injectedFailures.Count.ShouldBe(expectedStatementCount);

            await using var conn = new MySqlConnection(builder.ConnectionString);
            var appliedScripts = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM migration_history");
            var completedStatements = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM migration_statement_history
                WHERE status = 'completed'
                """);

            appliedScripts.ShouldBe(expectedScriptCount);
            completedStatements.ShouldBe(expectedStatementCount);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AppliesImportedScriptsToMariaDb()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_migration_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var runner = new MariaDbMigrationRunner(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            await runner.ApplyConfiguredMigrationsAsync();

            await using var conn = new MySqlConnection(builder.ConnectionString);
            await conn.ExecuteAsync("""
                DELETE FROM migration_statement_history
                WHERE script_name = '042_metric_event_ids.sql';

                DELETE FROM migration_history
                WHERE script_name = '042_metric_event_ids.sql';
                """);

            // Simulate the old runner committing all of migration 042's DDL but disappearing
            // before it could record script history. The safe historical preconditions must
            // reconcile that pre-journal state without relying on the new statement ledger.
            await runner.ApplyConfiguredMigrationsAsync();

            var appliedCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM migration_history");
            var expectedCount = Directory.EnumerateFiles(
                Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"),
                "*.sql").Count();
            appliedCount.ShouldBe(expectedCount);

            var tableCount = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                  AND table_name IN ('repositories', 'nodes', 'edges', 'migration_history')
                """);
            tableCount.ShouldBe(4);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ApplyMigrationsAsync_RecoversCompletedButUnrecordedWikiAndQuarantinesAmbiguousEmbeddings()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var migrationsPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"cg_mig_legacy_retry_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        try
        {
            var runner = CreateRunner(builder.ConnectionString, migrationsPath);
            await runner.ApplyConfiguredMigrationsAsync();

            await using var conn = new MySqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO wiki_pages (
                    section_id, parent_id, slug, title, content, author, revision,
                    sort_order, is_auto_generated, depth)
                SELECT id, NULL, 'legacy-retry-probe', 'Probe', 'Preserve me', 'test', 1, 0, FALSE, 0
                FROM wiki_sections
                WHERE slug = 'conventions'
                ON DUPLICATE KEY UPDATE content = VALUES(content);

                INSERT INTO embeddings (
                    entity_type, entity_key, embedding_json, model_name, dimensions)
                VALUES ('test', 'post-crash', '[1.0,0.0]', 'test-model', 2)
                ON DUPLICATE KEY UPDATE embedding_json = VALUES(embedding_json);

                DELETE FROM migration_statement_history
                WHERE script_name IN ('008_wiki.sql', '051_embedding_provenance_cutover.sql');

                DELETE FROM migration_history
                WHERE script_name IN ('008_wiki.sql', '051_embedding_provenance_cutover.sql');
                """);

            // An old runner may have committed the full script, including dropping the
            // convention sources or cutting over embeddings, before recording history.
            await runner.ApplyConfiguredMigrationsAsync();

            var wikiProbeCount = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM wiki_pages WHERE slug = 'legacy-retry-probe'
                """);
            var embeddingProbeCount = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM embeddings
                WHERE entity_type = 'test'
                  AND entity_key = 'post-crash'
                  AND model_name = 'legacy-unknown'
                  AND dimensions = 0
                """);
            var legacySourceCount = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                  AND table_name IN ('convention_pages', 'convention_revisions')
                """);

            wikiProbeCount.ShouldBe(1);
            embeddingProbeCount.ShouldBe(1);
            legacySourceCount.ShouldBe(0);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task ApplyMigrationsAsync_QuarantinesLegacyVectorsDefaultFilledByPartialOld051Alter()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"cg_mig_051_partial_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var migration051Path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations/051_embedding_provenance_cutover.sql"));
        var migrationsPath = CreateMigrationDirectory(
            "051_embedding_provenance_cutover.sql",
            await File.ReadAllTextAsync(migration051Path));

        try
        {
            var adminBuilder = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
            await using (var adminConn = new MySqlConnection(adminBuilder.ConnectionString))
            {
                await adminConn.OpenAsync();
                await adminConn.ExecuteAsync($"CREATE DATABASE `{databaseName}`");
            }

            await using (var conn = new MySqlConnection(builder.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync("""
                    CREATE TABLE embeddings (
                        entity_type VARCHAR(100) NOT NULL,
                        entity_key VARCHAR(500) NOT NULL,
                        embedding_json LONGTEXT NOT NULL,
                        updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
                        PRIMARY KEY (entity_type, entity_key)
                    ) ENGINE=InnoDB;

                    INSERT INTO embeddings (entity_type, entity_key, embedding_json)
                    VALUES ('legacy', 'stale-vector', '[1.0,0.0]');

                    ALTER TABLE embeddings
                        ADD COLUMN model_name VARCHAR(100) NOT NULL DEFAULT 'nomic-embed-text-v1.5' AFTER embedding_json,
                        ADD COLUMN dimensions INT NOT NULL DEFAULT 768 AFTER model_name;
                    """);

                // This is the exact old-runner checkpoint: the first ALTER committed and
                // default-filled stale rows, then the process disappeared before TRUNCATE
                // and before migration_history was written.
                var mislabeledCount = await conn.ExecuteScalarAsync<int>("""
                    SELECT COUNT(*)
                    FROM embeddings
                    WHERE model_name = 'nomic-embed-text-v1.5' AND dimensions = 768
                    """);
                mislabeledCount.ShouldBe(1);
            }

            var runner = CreateRunner(builder.ConnectionString, migrationsPath);
            await runner.ApplyConfiguredMigrationsAsync();

            await using (var conn = new MySqlConnection(builder.ConnectionString))
            {
                var quarantinedCount = await conn.ExecuteScalarAsync<int>("""
                    SELECT COUNT(*)
                    FROM embeddings
                    WHERE entity_type = 'legacy'
                      AND entity_key = 'stale-vector'
                      AND model_name = 'legacy-unknown'
                      AND dimensions = 0
                    """);
                quarantinedCount.ShouldBe(1);
            }

            var vectorStore = new MySqlVectorStore(
                Options.Create(new MariaDbStorageOptions { ConnectionString = builder.ConnectionString }),
                Options.Create(new CodeGraphStorageOptions
                {
                    EmbeddingModelName = "nomic-embed-text-v1.5",
                    EmbeddingDimensions = 768
                }));
            var currentModelResults = await vectorStore.SearchSimilarAsync([1.0f, 0.0f]);
            currentModelResults.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(migrationsPath, recursive: true);
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = ""
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }

    private static MariaDbMigrationRunner CreateRunner(string connectionString, string migrationsPath)
    {
        return new MariaDbMigrationRunner(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = connectionString,
                MigrationsPath = migrationsPath,
                MigrationLockTimeoutSeconds = 30
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);
    }

    private static string CreateMigrationDirectory(string fileName, string sql)
    {
        var path = Path.Combine(Path.GetTempPath(), $"codegraph-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, fileName), sql);
        return path;
    }

    private sealed class InjectedMigrationFailureException(int statementOrdinal)
        : Exception($"Injected failure after statement {statementOrdinal}.");
}
