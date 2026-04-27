using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbMigrationRunnerTests
{
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
    public async Task ApplyMigrationsAsync_AppliesImportedScriptsToMariaDbWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

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
}
