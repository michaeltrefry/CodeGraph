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

public class MariaDbJobScheduleStoreTests
{
    [Fact]
    public void MySqlJobScheduleStore_ImplementsStandaloneJobScheduleContract()
    {
        typeof(IJobScheduleStore).IsAssignableFrom(typeof(MySqlJobScheduleStore)).ShouldBeTrue();
    }

    [Fact]
    public void Model_MapsJobSchedulesToStandaloneMariaDbSchema()
    {
        using var context = new CodeGraphDbContext(CreateOptions(
            "Server=localhost;Database=codegraph;User ID=root;Password=test"));

        var schedule = context.Model.FindEntityType(typeof(JobScheduleEntity));
        schedule.ShouldNotBeNull();
        schedule.GetTableName().ShouldBe("job_schedules");
        schedule.FindProperty(nameof(JobScheduleEntity.CronExpression))!
            .GetColumnName()
            .ShouldBe("cron_expression");
        schedule.GetIndexes()
            .Single(index => index.Properties.Select(p => p.Name).SequenceEqual([nameof(JobScheduleEntity.Name)]))
            .IsUnique
            .ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlJobScheduleStore_RoundTripsAndLeasesSchedulesWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_job_schedule_test_{Guid.NewGuid():N}";
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
            var store = new MySqlJobScheduleStore(context);
            var now = TrimToSecond(DateTime.UtcNow);

            var created = await store.CreateScheduleAsync(new JobScheduleEntity
            {
                Name = "discover",
                JobType = "discover-repositories",
                IsEnabled = true,
                CronExpression = "*/5 * * * *",
                TimeZoneId = "UTC",
                ArgsJson = """{"source":"test"}""",
                NextRunUtc = now.AddMinutes(-1),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            created.Id.ShouldBeGreaterThan(0);
            (await store.ListSchedulesAsync()).Single().Name.ShouldBe("discover");
            (await store.GetScheduleByNameAsync("discover"))!.JobType.ShouldBe("discover-repositories");

            created.CronExpression = "0 * * * *";
            await store.UpdateScheduleAsync(created);
            (await store.GetScheduleByIdAsync(created.Id))!.CronExpression.ShouldBe("0 * * * *");

            var acquired = await store.TryAcquireDueScheduleAsync(now, "worker-a", TimeSpan.FromMinutes(2));
            acquired.ShouldNotBeNull();
            acquired.Id.ShouldBe(created.Id);
            acquired.LeaseOwner.ShouldBe("worker-a");

            (await store.TryAcquireScheduleAsync(created.Id, now.AddSeconds(30), "worker-b", TimeSpan.FromMinutes(2)))
                .ShouldBeNull();

            await store.MarkRunStartedAsync(created.Id, now.AddSeconds(1), "worker-a");
            (await store.GetScheduleByIdAsync(created.Id))!.LastRunStatus.ShouldBe("running");

            var nextRun = now.AddMinutes(5);
            await store.MarkRunCompletedAsync(created.Id, now.AddSeconds(5), nextRun, "completed", null, "worker-a");
            var completed = await store.GetScheduleByIdAsync(created.Id);
            completed.ShouldNotBeNull();
            completed.LastRunStatus.ShouldBe("completed");
            completed.LeaseOwner.ShouldBeNull();
            completed.NextRunUtc.ShouldBe(nextRun);

            await store.DeleteScheduleAsync(created.Id);
            (await store.GetScheduleByIdAsync(created.Id)).ShouldBeNull();
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

    private static DateTime TrimToSecond(DateTime value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

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
