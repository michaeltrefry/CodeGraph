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

public class MariaDbMetricsStoreTests
{
    [Fact]
    public void MySqlMetricsStore_ImplementsStandaloneMetricsContract()
    {
        typeof(IMetricsStore).IsAssignableFrom(typeof(MySqlMetricsStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlMetricsStore_RoundTripsMetricsHealthAndSecurityWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_metrics_store_test_{Guid.NewGuid():N}";
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
            var store = new MySqlMetricsStore(context);
            var now = DateTime.UtcNow;

            await store.UpsertFileMetricsBatchAsync("CodeGraph",
            [
                new FileMetricsEntity
                {
                    FilePath = "src/App.cs",
                    DotnetProject = "CodeGraph.Api",
                    Changes = 5,
                    LinesAdded = 100,
                    LinesRemoved = 25,
                    AuthorCount = 2,
                    ComplexityScore = 12,
                    RiskScore = 8.5,
                    HealthScore = 4.2,
                    Role = "core",
                    ComputedAt = now
                },
                new FileMetricsEntity
                {
                    FilePath = "src/Quiet.cs",
                    DotnetProject = "CodeGraph.Api",
                    RiskScore = 1.0,
                    HealthScore = 9.0,
                    Role = "leaf",
                    ComputedAt = now
                }
            ]);

            (await store.GetFileMetricsAsync("CodeGraph")).Count.ShouldBe(2);
            (await store.GetFileMetricsAsync("CodeGraph", "CodeGraph.Api")).Count.ShouldBe(2);
            (await store.GetHotspotsAsync("CodeGraph", 1)).Single().FilePath.ShouldBe("src/App.cs");

            await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
            {
                Project = "CodeGraph",
                OverallHealth = 7.5,
                TotalFiles = 2,
                HotspotCount = 1,
                ComputedAt = now
            });

            await store.UpsertProjectHealthAnalysisAsync(new ProjectHealthAnalysisEntity
            {
                Project = "CodeGraph",
                Analysis = "Healthy enough",
                Confidence = "high",
                CreatedAt = now,
                UpdatedAt = now
            });

            (await store.GetProjectHealthSummariesAsync("CodeGraph")).Single().DotnetProject.ShouldBe("");
            (await store.GetAllRepoHealthSummariesAsync()).Single().Project.ShouldBe("CodeGraph");
            (await store.GetProjectHealthAnalysesAsync("CodeGraph")).Single().Analysis.ShouldBe("Healthy enough");

            await store.UpsertSecurityFindingsBatchAsync("CodeGraph",
            [
                new SecurityFindingEntity
                {
                    Category = "dependency",
                    Severity = "high",
                    Title = "Package issue",
                    Description = "Needs update",
                    Package = "Example",
                    PackageVersion = "1.0.0",
                    ComputedAt = now
                }
            ]);

            await store.UpsertProjectSecuritySummaryAsync(new ProjectSecuritySummaryEntity
            {
                Project = "CodeGraph",
                SecurityScore = 8,
                HighCount = 1,
                ComputedAt = now
            });

            (await store.GetSecurityFindingsAsync("CodeGraph")).Single().Severity.ShouldBe("high");
            (await store.GetProjectSecuritySummaryAsync("CodeGraph"))!.HighCount.ShouldBe(1);

            await store.DeleteSecurityFindingsAsync("CodeGraph");
            (await store.GetSecurityFindingsAsync("CodeGraph")).ShouldBeEmpty();

            await store.DeleteFileMetricsAsync("CodeGraph");
            (await store.GetFileMetricsAsync("CodeGraph")).ShouldBeEmpty();
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
