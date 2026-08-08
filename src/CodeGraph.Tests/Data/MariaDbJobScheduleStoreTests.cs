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
        schedule.FindProperty(nameof(JobScheduleEntity.ScheduleRevision))!
            .GetColumnName()
            .ShouldBe("schedule_revision");
        schedule.GetIndexes()
            .Single(index => index.Properties.Select(p => p.Name).SequenceEqual([nameof(JobScheduleEntity.Name)]))
            .IsUnique
            .ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlJobScheduleStore_RoundTripsAndLeasesSchedulesWhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

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
            (await store.UpdateScheduleAsync(created)).ShouldBeTrue();
            var configured = await store.GetScheduleByIdAsync(created.Id);
            configured.ShouldNotBeNull();
            configured.CronExpression.ShouldBe("0 * * * *");
            configured.ScheduleRevision.ShouldBe(1);

            var acquired = await store.TryAcquireDueScheduleAsync(now, "worker-a", TimeSpan.FromMinutes(2));
            acquired.ShouldNotBeNull();
            acquired.Id.ShouldBe(created.Id);
            acquired.LeaseOwner.ShouldBe("worker-a");
            acquired.ScheduleRevision.ShouldBe(1);
            var staleConfiguration = await store.GetScheduleByIdAsync(created.Id);
            staleConfiguration.ShouldNotBeNull();

            (await store.TryAcquireScheduleAsync(created.Id, now.AddSeconds(30), "worker-b", TimeSpan.FromMinutes(2)))
                .ShouldBeNull();

            (await store.MarkRunStartedAsync(
                created.Id, now.AddSeconds(1), now.AddSeconds(1), "worker-a")).ShouldBeTrue();
            (await store.GetScheduleByIdAsync(created.Id))!.LastRunStatus.ShouldBe("running");
            (await store.RenewLeaseAsync(
                created.Id, now.AddMinutes(1), "worker-a", TimeSpan.FromMinutes(2))).ShouldBeTrue();

            staleConfiguration.Name = "discover-updated";
            staleConfiguration.IsEnabled = false;
            var editedNextRun = now.AddMinutes(45);
            staleConfiguration.NextRunUtc = editedNextRun;
            staleConfiguration.UpdatedAtUtc = now.AddMinutes(1).AddSeconds(1);
            (await store.UpdateScheduleAsync(staleConfiguration)).ShouldBeTrue();
            var updatedWhileRunning = await store.GetScheduleByIdAsync(created.Id);
            updatedWhileRunning.ShouldNotBeNull();
            updatedWhileRunning.Name.ShouldBe("discover-updated");
            updatedWhileRunning.IsEnabled.ShouldBeFalse();
            updatedWhileRunning.LastRunStatus.ShouldBe("running");
            updatedWhileRunning.LeaseOwner.ShouldBe("worker-a");
            updatedWhileRunning.LeaseExpiresUtc.ShouldBe(now.AddMinutes(3));
            updatedWhileRunning.NextRunUtc.ShouldBe(editedNextRun);
            updatedWhileRunning.ScheduleRevision.ShouldBe(2);
            var staleRunningState = CloneForConfigurationTest(updatedWhileRunning);

            var nextRun = now.AddMinutes(5);
            (await store.MarkRunCompletedAsync(
                created.Id, now.AddMinutes(1).AddSeconds(5), nextRun,
                "completed", null, now.AddMinutes(1).AddSeconds(5),
                acquired.ScheduleRevision, "worker-a")).ShouldBeTrue();
            var completed = await store.GetScheduleByIdAsync(created.Id);
            completed.ShouldNotBeNull();
            completed.LastRunStatus.ShouldBe("completed");
            completed.LeaseOwner.ShouldBeNull();
            completed.NextRunUtc.ShouldBe(editedNextRun);
            completed.ScheduleRevision.ShouldBe(3);

            staleRunningState.Name = "discover-after-completion";
            staleRunningState.UpdatedAtUtc = now.AddMinutes(1).AddSeconds(6);
            (await store.UpdateScheduleAsync(staleRunningState)).ShouldBeFalse();
            var editedAfterCompletion = await store.GetScheduleByIdAsync(created.Id);
            editedAfterCompletion.ShouldNotBeNull();
            editedAfterCompletion.Name.ShouldBe("discover-updated");
            editedAfterCompletion.LastRunStatus.ShouldBe("completed");
            editedAfterCompletion.LeaseOwner.ShouldBeNull();
            editedAfterCompletion.LeaseExpiresUtc.ShouldBeNull();
            editedAfterCompletion.NextRunUtc.ShouldBe(editedNextRun);

            var reacquired = await store.TryAcquireScheduleAsync(
                created.Id, now.AddMinutes(6), "worker-b", TimeSpan.FromMinutes(2));
            reacquired.ShouldNotBeNull();
            reacquired.ScheduleRevision.ShouldBe(3);
            var staleBeforeSecondCompletion = await store.GetScheduleByIdAsync(created.Id);
            staleBeforeSecondCompletion.ShouldNotBeNull();
            (await store.MarkRunCompletedAsync(
                created.Id, now.AddMinutes(6).AddSeconds(1), null,
                "failed", "stale completion", now.AddMinutes(6).AddSeconds(1),
                acquired.ScheduleRevision, "worker-a")).ShouldBeFalse();
            (await store.MarkRunCompletedAsync(
                created.Id, now.AddMinutes(6).AddSeconds(1), null,
                "completed", null, now.AddMinutes(8),
                reacquired.ScheduleRevision, "worker-b")).ShouldBeFalse();
            var completionNextRun = now.AddMinutes(30);
            (await store.MarkRunCompletedAsync(
                created.Id, now.AddMinutes(6).AddSeconds(2), completionNextRun,
                "completed", null, now.AddMinutes(7),
                reacquired.ScheduleRevision, "worker-b")).ShouldBeTrue();
            staleBeforeSecondCompletion.NextRunUtc = now.AddMinutes(7);
            staleBeforeSecondCompletion.UpdatedAtUtc = now.AddMinutes(7).AddSeconds(1);
            (await store.UpdateScheduleAsync(staleBeforeSecondCompletion)).ShouldBeFalse();
            var secondCompletion = await store.GetScheduleByIdAsync(created.Id);
            secondCompletion.ShouldNotBeNull();
            secondCompletion.NextRunUtc.ShouldBe(completionNextRun);
            secondCompletion.ScheduleRevision.ShouldBe(4);

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

    private static JobScheduleEntity CloneForConfigurationTest(JobScheduleEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        JobType = entity.JobType,
        IsEnabled = entity.IsEnabled,
        CronExpression = entity.CronExpression,
        TimeZoneId = entity.TimeZoneId,
        ArgsJson = entity.ArgsJson,
        NextRunUtc = entity.NextRunUtc,
        ScheduleRevision = entity.ScheduleRevision,
        LastRunStartedUtc = entity.LastRunStartedUtc,
        LastRunCompletedUtc = entity.LastRunCompletedUtc,
        LastRunStatus = entity.LastRunStatus,
        LastError = entity.LastError,
        LeaseAcquiredUtc = entity.LeaseAcquiredUtc,
        LeaseOwner = entity.LeaseOwner,
        LeaseExpiresUtc = entity.LeaseExpiresUtc,
        CreatedAtUtc = entity.CreatedAtUtc,
        UpdatedAtUtc = entity.UpdatedAtUtc
    };

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
