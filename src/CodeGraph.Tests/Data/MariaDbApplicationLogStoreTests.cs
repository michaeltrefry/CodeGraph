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

public class MariaDbApplicationLogStoreTests
{
    [Fact]
    public async Task Store_WritesFiltersPagesSearchesAndPrunesWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_application_log_test_{Guid.NewGuid():N}";
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
            var store = new MySqlApplicationLogStore(context);
            var now = DateTime.UtcNow;
            await store.WriteBatchAsync([
                Entry(now.AddMinutes(-3), "Warning", "cache miss"),
                Entry(now.AddMinutes(-2), "Error", "Repository TIMEOUT", "System.TimeoutException"),
                Entry(now.AddMinutes(-1), "Error", "different failure")
            ]);

            var result = await store.QueryAsync(new ApplicationLogQuery(
                Page: 1,
                PageSize: 100,
                Level: "Error",
                StartUtc: now.AddMinutes(-2.5),
                EndUtc: now,
                Search: "timeout"));

            result.TotalCount.ShouldBe(1);
            result.Entries.Single().Message.ShouldBe("Repository TIMEOUT");
            (await store.DeleteBeforeAsync(now.AddMinutes(-1.5))).ShouldBe(2);
            (await store.QueryAsync(new ApplicationLogQuery(1, 100, null, null, null, null)))
                .Entries.Single().Message.ShouldBe("different failure");
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static ApplicationLogEntryEntity Entry(
        DateTime occurredAtUtc,
        string level,
        string message,
        string? exception = null) => new()
    {
        OccurredAtUtc = occurredAtUtc,
        Level = level,
        Source = "CodeGraph.Api@test",
        Category = "CodeGraph.Tests",
        Message = message,
        Exception = exception
    };

    private static DbContextOptions<CodeGraphDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(
                connectionString,
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }
}
