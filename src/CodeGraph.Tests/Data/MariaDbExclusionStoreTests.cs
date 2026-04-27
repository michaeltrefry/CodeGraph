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

public class MariaDbExclusionStoreTests
{
    [Fact]
    public void MySqlExclusionStore_ImplementsStandaloneExclusionContract()
    {
        typeof(IExclusionStore).IsAssignableFrom(typeof(MySqlExclusionStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlExclusionStore_RoundTripsRulesAndSecretPathsWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_exclusion_store_test_{Guid.NewGuid():N}";
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

            var options = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(
                    builder.ConnectionString,
                    ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;

            await using var context = new CodeGraphDbContext(options);
            var store = new MySqlExclusionStore(context);
            var now = DateTime.UtcNow;

            var rule = await store.CreateExclusionRuleAsync(new ExclusionRuleEntity
            {
                TargetType = "file",
                TargetValue = "appsettings.Development.json",
                ExclusionType = "secret",
                Reason = "test",
                CreatedBy = "codex",
                CreatedAt = now,
                UpdatedAt = now
            });

            rule.Id.ShouldBeGreaterThan(0);
            (await store.ListExclusionRulesAsync()).Single().TargetValue.ShouldBe(rule.TargetValue);
            (await store.GetExclusionRuleAsync(rule.Id))!.ExclusionType.ShouldBe("secret");

            var updated = await store.UpdateExclusionRuleAsync(rule.Id, "generated", "updated");
            updated.ShouldNotBeNull();
            updated.ExclusionType.ShouldBe("generated");
            updated.Reason.ShouldBe("updated");

            context.SecurityFindings.Add(new SecurityFindingEntity
            {
                Project = "CodeGraph",
                Category = "secret",
                Severity = "high",
                Title = "Secret",
                Description = "Secret",
                FilePath = "appsettings.Development.json",
                ComputedAt = now
            });
            await context.SaveChangesAsync();

            var secretPaths = await store.GetSecretFilePathsAsync("CodeGraph");
            secretPaths.ShouldContain("appsettings.Development.json");

            (await store.DeleteExclusionRuleAsync(rule.Id)).ShouldBeTrue();
            (await store.GetExclusionRuleAsync(rule.Id)).ShouldBeNull();
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
