using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbDbHealthStoreTests
{
    [Fact]
    public void MySqlDbHealthStore_ImplementsStandaloneDbHealthContract()
    {
        typeof(IDbHealthStore).IsAssignableFrom(typeof(MySqlDbHealthStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlDbHealthStore_ReturnsHealthyStatusAfterMigrationsWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_db_health_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var options = new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        };

        var runner = new MariaDbMigrationRunner(
            Options.Create(options),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var store = new MySqlDbHealthStore(Options.Create(options));
            var health = await store.GetDatabaseHealthAsync();

            health.Status.ShouldBe(
                "healthy",
                $"Missing constraints: {string.Join(", ", health.MissingConstraints)}; missing indexes: {string.Join(", ", health.MissingIndexes)}");
            health.MissingConstraints.ShouldBeEmpty();
            health.MissingIndexes.ShouldBeEmpty();
            health.OfflineIndexes.ShouldBeEmpty();
            health.DuplicateGroups.ShouldBeEmpty();
            health.ConstraintCount.ShouldBeGreaterThanOrEqualTo(health.ExpectedConstraintCount);
            health.IndexCount.ShouldBeGreaterThanOrEqualTo(health.ExpectedIndexCount);
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
