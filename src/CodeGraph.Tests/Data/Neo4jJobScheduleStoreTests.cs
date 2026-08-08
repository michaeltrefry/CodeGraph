using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class Neo4jJobScheduleStoreTests
{
    [Fact]
    public async Task Neo4jJobScheduleStore_AcquisitionAllowsExactlyOneWinnerUnderContention()
    {
        var connection = Neo4jJobScheduleTestEnvironment.RequireConnection();
        await using var firstFactory = CreateFactory(connection);
        await using var secondFactory = CreateFactory(connection);
        var stores = new[]
        {
            new Neo4jJobScheduleStore(firstFactory),
            new Neo4jJobScheduleStore(secondFactory)
        };
        var now = TrimToSecond(DateTime.UtcNow);
        var created = await stores[0].CreateScheduleAsync(new JobScheduleEntity
        {
            Name = $"lease-contention-{Guid.NewGuid():N}",
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
            var manualResults = await ContendAsync((store, worker) =>
                store.TryAcquireScheduleAsync(
                    created.Id, now, worker, TimeSpan.FromMinutes(2)));
            manualResults.Count(result => result is not null).ShouldBe(1);
            var manualWinner = manualResults.Single(result => result is not null)!;

            (await stores[0].MarkRunCompletedAsync(
                created.Id,
                now.AddSeconds(1),
                null,
                "completed",
                null,
                now.AddSeconds(1),
                manualWinner.ScheduleRevision,
                manualWinner.LeaseOwner!)).ShouldBeTrue();

            var dueResults = await ContendAsync((store, worker) =>
                store.TryAcquireDueScheduleAsync(
                    now.AddSeconds(2), worker, TimeSpan.FromMinutes(2)));
            dueResults.Count(result => result is not null).ShouldBe(1);
        }
        finally
        {
            await stores[0].DeleteScheduleAsync(created.Id);
        }

        async Task<JobScheduleEntity?[]> ContendAsync(
            Func<Neo4jJobScheduleStore, string, Task<JobScheduleEntity?>> acquire)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var contenders = Enumerable.Range(0, 64)
                .Select(async index =>
                {
                    await start.Task;
                    return await acquire(stores[index % stores.Length], $"worker-{index}");
                })
                .ToArray();
            start.SetResult();
            return await Task.WhenAll(contenders);
        }
    }

    [Fact]
    public async Task Neo4jJobScheduleStore_FencedTransitionsRemainAtomicUnderTwoStoreRaces()
    {
        var connection = Neo4jJobScheduleTestEnvironment.RequireConnection();
        await using var firstFactory = CreateFactory(connection);
        await using var secondFactory = CreateFactory(connection);
        var firstStore = new Neo4jJobScheduleStore(firstFactory);
        var secondStore = new Neo4jJobScheduleStore(secondFactory);
        var now = TrimToSecond(DateTime.UtcNow);
        var created = await firstStore.CreateScheduleAsync(new JobScheduleEntity
        {
            Name = $"lease-transition-race-{Guid.NewGuid():N}",
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
            for (var iteration = 0; iteration < 16; iteration++)
            {
                var acquiredAt = now.AddHours(iteration);
                var originalToken = $"renew-original-{iteration}";
                var successorToken = $"renew-successor-{iteration}";
                var acquired = await firstStore.TryAcquireScheduleAsync(
                    created.Id, acquiredAt, originalToken, TimeSpan.FromMinutes(2));
                acquired.ShouldNotBeNull();

                var (renewed, successor) = await RaceAsync(
                    () => firstStore.RenewLeaseAsync(
                        created.Id,
                        acquiredAt.AddSeconds(119),
                        originalToken,
                        TimeSpan.FromMinutes(2)),
                    () => secondStore.TryAcquireScheduleAsync(
                        created.Id,
                        acquiredAt.AddSeconds(121),
                        successorToken,
                        TimeSpan.FromMinutes(2)));

                (renewed ? 1 : 0).ShouldBe(successor is null ? 1 : 0);
                var current = await firstStore.GetScheduleByIdAsync(created.Id);
                current.ShouldNotBeNull();
                current.LeaseOwner.ShouldBe(renewed ? originalToken : successorToken);
                (await firstStore.MarkRunCompletedAsync(
                    created.Id,
                    acquiredAt.AddMinutes(3),
                    null,
                    "completed",
                    null,
                    acquiredAt.AddMinutes(3),
                    current.ScheduleRevision,
                    current.LeaseOwner!)).ShouldBeTrue();
            }

            for (var iteration = 0; iteration < 16; iteration++)
            {
                var acquiredAt = now.AddDays(1).AddHours(iteration);
                var originalToken = $"completion-original-{iteration}";
                var successorToken = $"completion-successor-{iteration}";
                var acquired = await firstStore.TryAcquireScheduleAsync(
                    created.Id, acquiredAt, originalToken, TimeSpan.FromMinutes(1));
                acquired.ShouldNotBeNull();

                var (_, successor) = await RaceAsync(
                    () => firstStore.MarkRunCompletedAsync(
                        created.Id,
                        acquiredAt.AddSeconds(59),
                        null,
                        "completed",
                        null,
                        acquiredAt.AddSeconds(59),
                        acquired.ScheduleRevision,
                        originalToken),
                    () => secondStore.TryAcquireScheduleAsync(
                        created.Id,
                        acquiredAt.AddSeconds(61),
                        successorToken,
                        TimeSpan.FromMinutes(2)));

                successor.ShouldNotBeNull();
                var current = await firstStore.GetScheduleByIdAsync(created.Id);
                current.ShouldNotBeNull();
                current.LeaseOwner.ShouldBe(successorToken);
                (await firstStore.MarkRunStartedAsync(
                    created.Id,
                    acquiredAt.AddSeconds(62),
                    acquiredAt.AddSeconds(62),
                    originalToken)).ShouldBeFalse();
                (await firstStore.MarkRunCompletedAsync(
                    created.Id,
                    acquiredAt.AddSeconds(63),
                    null,
                    "completed",
                    null,
                    acquiredAt.AddSeconds(63),
                    successor.ScheduleRevision,
                    successorToken)).ShouldBeTrue();
            }

            for (var iteration = 0; iteration < 16; iteration++)
            {
                var acquiredAt = now.AddDays(2).AddHours(iteration);
                var originalToken = $"start-original-{iteration}";
                var successorToken = $"start-successor-{iteration}";
                var acquired = await firstStore.TryAcquireScheduleAsync(
                    created.Id, acquiredAt, originalToken, TimeSpan.FromMinutes(1));
                acquired.ShouldNotBeNull();

                var (_, successor) = await RaceAsync(
                    () => firstStore.MarkRunStartedAsync(
                        created.Id,
                        acquiredAt.AddSeconds(59),
                        acquiredAt.AddSeconds(59),
                        originalToken),
                    () => secondStore.TryAcquireScheduleAsync(
                        created.Id,
                        acquiredAt.AddSeconds(61),
                        successorToken,
                        TimeSpan.FromMinutes(2)));

                successor.ShouldNotBeNull();
                var current = await firstStore.GetScheduleByIdAsync(created.Id);
                current.ShouldNotBeNull();
                current.LeaseOwner.ShouldBe(successorToken);
                (await firstStore.MarkRunCompletedAsync(
                    created.Id,
                    acquiredAt.AddSeconds(62),
                    null,
                    "completed",
                    null,
                    acquiredAt.AddSeconds(62),
                    successor.ScheduleRevision,
                    successorToken)).ShouldBeTrue();
            }

            for (var iteration = 0; iteration < 16; iteration++)
            {
                var acquiredAt = now.AddDays(3).AddHours(iteration);
                var token = $"revision-owner-{iteration}";
                var acquired = await firstStore.TryAcquireScheduleAsync(
                    created.Id, acquiredAt, token, TimeSpan.FromMinutes(2));
                acquired.ShouldNotBeNull();
                var staleConfiguration = await firstStore.GetScheduleByIdAsync(created.Id);
                staleConfiguration.ShouldNotBeNull();
                var configuredNextRun = acquiredAt.AddHours(1);
                var completionNextRun = acquiredAt.AddHours(2);
                staleConfiguration.NextRunUtc = configuredNextRun;
                staleConfiguration.UpdatedAtUtc = acquiredAt.AddSeconds(1);

                var (configurationUpdated, completed) = await RaceAsync(
                    () => firstStore.UpdateScheduleAsync(staleConfiguration),
                    () => secondStore.MarkRunCompletedAsync(
                        created.Id,
                        acquiredAt.AddSeconds(1),
                        completionNextRun,
                        "completed",
                        null,
                        acquiredAt.AddSeconds(1),
                        acquired.ScheduleRevision,
                        token));

                completed.ShouldBeTrue();
                var current = await firstStore.GetScheduleByIdAsync(created.Id);
                current.ShouldNotBeNull();
                current.LeaseOwner.ShouldBeNull();
                current.ScheduleRevision.ShouldBe(
                    acquired.ScheduleRevision + (configurationUpdated ? 2 : 1));
                current.NextRunUtc.ShouldBe(
                    configurationUpdated ? configuredNextRun : completionNextRun);
            }
        }
        finally
        {
            await firstStore.DeleteScheduleAsync(created.Id);
        }
    }

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

    private static Neo4jSessionFactory CreateFactory((string Uri, string Username, string Password) connection)
        => new(Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = connection.Uri,
            Neo4jUsername = connection.Username,
            Neo4jPassword = connection.Password
        }));

    private static async Task<(TFirst First, TSecond Second)> RaceAsync<TFirst, TSecond>(
        Func<Task<TFirst>> first,
        Func<Task<TSecond>> second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = Task.Run(async () =>
        {
            await start.Task;
            return await first();
        });
        var secondTask = Task.Run(async () =>
        {
            await start.Task;
            return await second();
        });
        start.SetResult();
        await Task.WhenAll(firstTask, secondTask);
        return (await firstTask, await secondTask);
    }

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
