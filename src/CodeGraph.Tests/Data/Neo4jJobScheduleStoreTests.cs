using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class Neo4jJobScheduleStoreTests
{
    [Fact]
    public async Task Neo4jJobScheduleStore_RenewsFencesAndPreservesRuntimeStateDuringConfigurationUpdates()
    {
        var (uri, username, password) = Neo4jJobScheduleTestEnvironment.RequireConnection();
        await using var factory = new Neo4jSessionFactory(Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = uri,
            Neo4jUsername = username,
            Neo4jPassword = password
        }));
        var store = new Neo4jJobScheduleStore(factory);
        var now = TrimToSecond(DateTime.UtcNow);
        var created = await store.CreateScheduleAsync(new JobScheduleEntity
        {
            Name = $"lease-test-{Guid.NewGuid():N}",
            JobType = "discover-repositories",
            IsEnabled = true,
            CronExpression = "*/5 * * * *",
            TimeZoneId = "UTC",
            ArgsJson = "{}",
            NextRunUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        try
        {
            var acquired = await store.TryAcquireDueScheduleAsync(
                now, "worker-a", TimeSpan.FromMinutes(2));
            acquired.ShouldNotBeNull();
            acquired.ScheduleRevision.ShouldBe(0);
            var staleConfiguration = await store.GetScheduleByIdAsync(created.Id);
            staleConfiguration.ShouldNotBeNull();

            (await store.MarkRunStartedAsync(
                created.Id, now.AddSeconds(1), now.AddSeconds(1), "worker-a")).ShouldBeTrue();
            (await store.RenewLeaseAsync(
                created.Id, now.AddMinutes(1), "worker-a", TimeSpan.FromMinutes(2))).ShouldBeTrue();

            staleConfiguration.Name = $"edited-{Guid.NewGuid():N}";
            staleConfiguration.IsEnabled = false;
            var editedNextRun = now.AddMinutes(45);
            staleConfiguration.NextRunUtc = editedNextRun;
            staleConfiguration.UpdatedAtUtc = now.AddMinutes(1).AddSeconds(1);
            (await store.UpdateScheduleAsync(staleConfiguration)).ShouldBeTrue();
            var updatedWhileRunning = await store.GetScheduleByIdAsync(created.Id);
            updatedWhileRunning.ShouldNotBeNull();
            updatedWhileRunning.Name.ShouldBe(staleConfiguration.Name);
            updatedWhileRunning.IsEnabled.ShouldBeFalse();
            updatedWhileRunning.LastRunStatus.ShouldBe("running");
            updatedWhileRunning.LeaseOwner.ShouldBe("worker-a");
            updatedWhileRunning.LeaseExpiresUtc.ShouldBe(now.AddMinutes(3));
            updatedWhileRunning.NextRunUtc.ShouldBe(editedNextRun);
            updatedWhileRunning.ScheduleRevision.ShouldBe(1);
            var staleRunningState = Clone(updatedWhileRunning);

            (await store.MarkRunCompletedAsync(
                created.Id,
                now.AddMinutes(1).AddSeconds(5),
                now.AddMinutes(5),
                "completed",
                null,
                now.AddMinutes(3),
                acquired.ScheduleRevision,
                "worker-a")).ShouldBeFalse();
            (await store.MarkRunCompletedAsync(
                created.Id,
                now.AddMinutes(1).AddSeconds(5),
                now.AddMinutes(5),
                "completed",
                null,
                now.AddMinutes(2),
                acquired.ScheduleRevision,
                "worker-a")).ShouldBeTrue();

            staleRunningState.Name = $"post-completion-{Guid.NewGuid():N}";
            staleRunningState.UpdatedAtUtc = now.AddMinutes(2).AddSeconds(1);
            (await store.UpdateScheduleAsync(staleRunningState)).ShouldBeFalse();
            var completed = await store.GetScheduleByIdAsync(created.Id);
            completed.ShouldNotBeNull();
            completed.Name.ShouldBe(staleConfiguration.Name);
            completed.LastRunStatus.ShouldBe("completed");
            completed.LeaseOwner.ShouldBeNull();
            completed.LeaseExpiresUtc.ShouldBeNull();
            completed.NextRunUtc.ShouldBe(editedNextRun);
            completed.ScheduleRevision.ShouldBe(2);

            var reacquired = await store.TryAcquireScheduleAsync(
                created.Id, now.AddMinutes(6), "worker-b", TimeSpan.FromMinutes(2));
            reacquired.ShouldNotBeNull();
            reacquired.ScheduleRevision.ShouldBe(2);
            var staleBeforeSecondCompletion = await store.GetScheduleByIdAsync(created.Id);
            staleBeforeSecondCompletion.ShouldNotBeNull();
            var completionNextRun = now.AddMinutes(30);
            (await store.MarkRunCompletedAsync(
                created.Id,
                now.AddMinutes(6).AddSeconds(1),
                completionNextRun,
                "completed",
                null,
                now.AddMinutes(7),
                reacquired.ScheduleRevision,
                "worker-b")).ShouldBeTrue();
            staleBeforeSecondCompletion.NextRunUtc = now.AddMinutes(7);
            staleBeforeSecondCompletion.UpdatedAtUtc = now.AddMinutes(7).AddSeconds(1);
            (await store.UpdateScheduleAsync(staleBeforeSecondCompletion)).ShouldBeFalse();
            var secondCompletion = await store.GetScheduleByIdAsync(created.Id);
            secondCompletion.ShouldNotBeNull();
            secondCompletion.NextRunUtc.ShouldBe(completionNextRun);
            secondCompletion.ScheduleRevision.ShouldBe(3);
        }
        finally
        {
            await store.DeleteScheduleAsync(created.Id);
        }
    }

    private static DateTime TrimToSecond(DateTime value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static JobScheduleEntity Clone(JobScheduleEntity entity) => new()
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
}

internal static class Neo4jJobScheduleTestEnvironment
{
    public static (string Uri, string Username, string Password) RequireConnection()
    {
        var uri = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_URI");
        var username = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_USERNAME");
        var password = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_TEST_PASSWORD");
        if (string.IsNullOrWhiteSpace(uri)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "CODEGRAPH_NEO4J_TEST_URI, CODEGRAPH_NEO4J_TEST_USERNAME, and "
                + "CODEGRAPH_NEO4J_TEST_PASSWORD are required for Neo4j integration tests.");
        }

        return (uri, username, password);
    }
}
