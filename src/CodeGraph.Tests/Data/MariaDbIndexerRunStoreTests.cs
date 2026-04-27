using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbIndexerRunStoreTests
{
    [Fact]
    public void MySqlIndexerRunStore_ImplementsStandaloneIndexerRunContract()
    {
        typeof(IIndexerRunStore).IsAssignableFrom(typeof(MySqlIndexerRunStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlIndexerRunStore_RoundTripsRunStatusWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_indexer_test_{Guid.NewGuid():N}";
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

            await using var context = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            var store = new MySqlIndexerRunStore(context);

            var runId = await store.CreateIndexerRunAsync(new IndexerRunEntity
            {
                Operation = "index",
                RequestedByUsername = "codex",
                Target = "CodeGraph",
                Status = "queued"
            });

            runId.ShouldBeGreaterThan(0);
            (await store.GetIndexerRunAsync(runId))!.Status.ShouldBe("queued");

            await store.UpdateIndexerRunStatusAsync(runId, "running", message: "started");
            var running = await store.GetIndexerRunAsync(runId);
            running.ShouldNotBeNull();
            running.Status.ShouldBe("running");
            running.Message.ShouldBe("started");
            running.StartedAt.ShouldNotBeNull();

            await store.UpdateIndexerRunStatusAsync(runId, "completed", completedAt: DateTime.UtcNow);
            var completed = await store.GetIndexerRunAsync(runId);
            completed.ShouldNotBeNull();
            completed.Status.ShouldBe("completed");
            completed.CompletedAt.ShouldNotBeNull();

            var recent = await store.ListIndexerRunsAsync(status: "completed", operation: "index", take: 5);
            recent.Count.ShouldBe(1);
            recent[0].Id.ShouldBe(runId);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static DbContextOptions<CodeGraphDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(
                connectionString,
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;

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
