using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.WikiRag;

namespace CodeGraph.Jobs.Tests.Jobs;

public class JobScheduleServiceTests
{
    [Fact]
    public async Task CreateAsync_ComputesNextRunAndNormalizesArgs()
    {
        var store = new InMemoryJobScheduleStore();
        var service = new JobScheduleService(store, CreateDispatcher(), NullLogger<JobScheduleService>.Instance);

        var created = await service.CreateAsync(new CreateJobScheduleRequest
        {
            Name = "Batch Poll",
            JobType = JobTypes.ProcessBatchAnalysis,
            CronExpression = "0 */6 * * *",
            TimeZoneId = "UTC",
            Args = JsonDocument.Parse("""{"repo":"Orders.Api"}""").RootElement
        });

        created.Id.ShouldBeGreaterThan(0);
        created.JobType.ShouldBe(JobTypes.ProcessBatchAnalysis);
        created.Args.GetProperty("repo").GetString().ShouldBe("Orders.Api");
        created.NextRunUtc.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task RunNowAsync_ThrowsWhenScheduleAlreadyLeased()
    {
        var store = new InMemoryJobScheduleStore();
        var service = new JobScheduleService(store, CreateDispatcher(), NullLogger<JobScheduleService>.Instance);
        var created = await service.CreateAsync(new CreateJobScheduleRequest
        {
            Name = "Busy schedule",
            JobType = JobTypes.ReIndexAll,
            CronExpression = "0 0 * * *",
            TimeZoneId = "UTC"
        });

        await store.TryAcquireScheduleAsync(created.Id, DateTime.UtcNow, "other-owner", TimeSpan.FromMinutes(15));

        await Should.ThrowAsync<InvalidOperationException>(() => service.RunNowAsync(created.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LongRunningManualAndScheduledJobs_RenewLeaseBeforeExpiry(bool manual)
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var clock = new ManualJobScheduleClock(now);
        var store = new InMemoryJobScheduleStore();
        var dispatcher = new BlockingDispatcher(clock);
        var service = new JobScheduleService(
            store, dispatcher, NullLogger<JobScheduleService>.Instance, clock);
        var created = await CreateScheduleAsync(service, store, now);

        Task runTask = manual
            ? service.RunNowAsync(created.Id)
            : service.TryRunNextDueScheduleAsync();
        await dispatcher.Started.Task;

        for (var renewal = 1; renewal <= 4; renewal++)
        {
            await WaitUntilAsync(() => clock.PendingDelayCount > 0);
            clock.Advance(TimeSpan.FromMinutes(5));
            await WaitUntilAsync(() => store.RenewalCount >= renewal);

            (await store.TryAcquireScheduleAsync(
                created.Id,
                clock.UtcNow,
                "competing-worker",
                TimeSpan.FromMinutes(15))).ShouldBeNull();
        }

        dispatcher.Complete();
        await runTask;

        var completed = await store.GetScheduleByIdAsync(created.Id);
        completed!.LastRunStatus.ShouldBe("succeeded");
        completed.LeaseOwner.ShouldBeNull();
    }

    [Fact]
    public async Task LeaseOwnershipLoss_CancelsWorkAndRaisesExplicitFailure()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var clock = new ManualJobScheduleClock(now);
        var store = new InMemoryJobScheduleStore();
        var dispatcher = new BlockingDispatcher(clock);
        var service = new JobScheduleService(
            store, dispatcher, NullLogger<JobScheduleService>.Instance, clock);
        var created = await CreateScheduleAsync(service, store, now);

        var runTask = service.RunNowAsync(created.Id);
        await dispatcher.Started.Task;
        store.ReplaceLease(created.Id, "new-owner", now.AddMinutes(30));

        await WaitUntilAsync(() => clock.PendingDelayCount > 0);
        clock.Advance(TimeSpan.FromMinutes(5));

        await Should.ThrowAsync<JobScheduleLeaseLostException>(async () => await runTask);
        dispatcher.CancellationObserved.ShouldBeTrue();
        (await store.GetScheduleByIdAsync(created.Id))!.LeaseOwner.ShouldBe("new-owner");
    }

    [Fact]
    public async Task CompletionAfterOwnershipLoss_IsFencedAndReported()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var clock = new ManualJobScheduleClock(now);
        var store = new InMemoryJobScheduleStore();
        var dispatcher = new BlockingDispatcher(clock);
        var service = new JobScheduleService(
            store, dispatcher, NullLogger<JobScheduleService>.Instance, clock);
        var created = await CreateScheduleAsync(service, store, now);

        var runTask = service.RunNowAsync(created.Id);
        await dispatcher.Started.Task;
        store.ReplaceLease(created.Id, "new-owner", now.AddMinutes(30));
        dispatcher.Complete();

        await Should.ThrowAsync<JobScheduleLeaseLostException>(async () => await runTask);
        var persisted = await store.GetScheduleByIdAsync(created.Id);
        persisted!.LeaseOwner.ShouldBe("new-owner");
        persisted.LastRunStatus.ShouldBe("running");
    }

    [Fact]
    public async Task ConfigurationUpdate_CannotOverwriteRenewalOrCompletionState()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var store = new InMemoryJobScheduleStore();
        var clock = new ManualJobScheduleClock(now);
        var service = new JobScheduleService(
            store, new BlockingDispatcher(clock), NullLogger<JobScheduleService>.Instance, clock);
        var created = await CreateScheduleAsync(service, store, now);

        var acquired = await store.TryAcquireScheduleAsync(
            created.Id, now, "worker-a", TimeSpan.FromMinutes(15));
        acquired.ShouldNotBeNull();
        var staleConfiguration = await store.GetScheduleByIdAsync(created.Id);
        staleConfiguration.ShouldNotBeNull();
        (await store.MarkRunStartedAsync(
            created.Id, now, now, "worker-a")).ShouldBeTrue();
        (await store.RenewLeaseAsync(
            created.Id, now.AddMinutes(5), "worker-a", TimeSpan.FromMinutes(15))).ShouldBeTrue();

        staleConfiguration.Name = "edited-while-running";
        staleConfiguration.IsEnabled = false;
        var editedNextRun = now.AddMinutes(45);
        staleConfiguration.NextRunUtc = editedNextRun;
        staleConfiguration.UpdatedAtUtc = now.AddMinutes(5).AddSeconds(1);
        (await store.UpdateScheduleAsync(staleConfiguration)).ShouldBeTrue();

        var renewed = await store.GetScheduleByIdAsync(created.Id);
        renewed.ShouldNotBeNull();
        renewed.Name.ShouldBe("edited-while-running");
        renewed.IsEnabled.ShouldBeFalse();
        renewed.LastRunStatus.ShouldBe("running");
        renewed.LeaseOwner.ShouldBe("worker-a");
        renewed.LeaseExpiresUtc.ShouldBe(now.AddMinutes(20));
        renewed.NextRunUtc.ShouldBe(editedNextRun);
        var staleRunningState = renewed.Clone();

        (await store.MarkRunCompletedAsync(
            created.Id,
            now.AddMinutes(6),
            now.AddMinutes(30),
            "succeeded",
            null,
            now.AddMinutes(6),
            acquired.ScheduleRevision,
            "worker-a")).ShouldBeTrue();
        staleRunningState.Name = "edited-after-completion";
        staleRunningState.UpdatedAtUtc = now.AddMinutes(6).AddSeconds(1);
        (await store.UpdateScheduleAsync(staleRunningState)).ShouldBeFalse();

        var completed = await store.GetScheduleByIdAsync(created.Id);
        completed.ShouldNotBeNull();
        completed.Name.ShouldBe("edited-while-running");
        completed.LastRunStatus.ShouldBe("succeeded");
        completed.LeaseOwner.ShouldBeNull();
        completed.LeaseExpiresUtc.ShouldBeNull();
        completed.NextRunUtc.ShouldBe(editedNextRun);
    }

    [Fact]
    public async Task CompletionBeforeStaleConfigurationUpdate_PreservesCompletionNextRun()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var store = new InMemoryJobScheduleStore();
        var clock = new ManualJobScheduleClock(now);
        var service = new JobScheduleService(
            store, new BlockingDispatcher(clock), NullLogger<JobScheduleService>.Instance, clock);
        var created = await CreateScheduleAsync(service, store, now);
        var acquired = await store.TryAcquireScheduleAsync(
            created.Id, now, "worker-a", TimeSpan.FromMinutes(15));
        acquired.ShouldNotBeNull();
        var staleConfiguration = await store.GetScheduleByIdAsync(created.Id);
        staleConfiguration.ShouldNotBeNull();
        (await store.MarkRunStartedAsync(
            created.Id, now, now, "worker-a")).ShouldBeTrue();

        var completionNextRun = now.AddMinutes(30);
        (await store.MarkRunCompletedAsync(
            created.Id,
            now.AddMinutes(1),
            completionNextRun,
            "succeeded",
            null,
            now.AddMinutes(1),
            acquired.ScheduleRevision,
            "worker-a")).ShouldBeTrue();

        staleConfiguration.NextRunUtc = now.AddMinutes(2);
        staleConfiguration.IsEnabled = false;
        staleConfiguration.UpdatedAtUtc = now.AddMinutes(1).AddSeconds(1);
        (await store.UpdateScheduleAsync(staleConfiguration)).ShouldBeFalse();

        var completed = await store.GetScheduleByIdAsync(created.Id);
        completed.ShouldNotBeNull();
        completed.NextRunUtc.ShouldBe(completionNextRun);
        completed.LastRunStatus.ShouldBe("succeeded");
        completed.LeaseOwner.ShouldBeNull();
    }

    [Fact]
    public async Task StaleHandlerCompletionTimestamp_CannotCompleteExpiredLease()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var clock = new ManualJobScheduleClock(now);
        var store = new InMemoryJobScheduleStore();
        var dispatcher = new StaleCompletionDispatcher(now.AddMinutes(1));
        var service = new JobScheduleService(
            store, dispatcher, NullLogger<JobScheduleService>.Instance, clock);
        var created = await CreateScheduleAsync(service, store, now);

        var runTask = service.RunNowAsync(created.Id);
        await dispatcher.Started.Task;
        clock.AdvanceWithoutCompletingDelays(TimeSpan.FromMinutes(16));
        dispatcher.Complete();

        await Should.ThrowAsync<JobScheduleLeaseLostException>(async () => await runTask);
        var persisted = await store.GetScheduleByIdAsync(created.Id);
        persisted.ShouldNotBeNull();
        persisted.LastRunStatus.ShouldBe("running");
        persisted.LeaseOwner.ShouldNotBeNull();
    }

    [Fact]
    public async Task LeaseLoss_CancelsRegenerateMcpDocsThroughRealDispatcher()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var clock = new ManualJobScheduleClock(now);
        var store = new InMemoryJobScheduleStore();
        var mcpDocs = new BlockingMcpDocService();
        var service = new JobScheduleService(
            store,
            CreateDispatcher(mcpDocs),
            NullLogger<JobScheduleService>.Instance,
            clock);
        var created = await service.CreateAsync(new CreateJobScheduleRequest
        {
            Name = "cancellable-mcp-docs",
            JobType = JobTypes.RegenerateMcpDocs,
            CronExpression = "*/5 * * * *",
            TimeZoneId = "UTC"
        });

        var runTask = service.RunNowAsync(created.Id);
        await mcpDocs.Started.Task;
        store.ReplaceLease(created.Id, "new-owner", now.AddMinutes(30));
        await WaitUntilAsync(() => clock.PendingDelayCount > 0);
        clock.Advance(TimeSpan.FromMinutes(5));

        await Should.ThrowAsync<JobScheduleLeaseLostException>(async () => await runTask);
        mcpDocs.CancellationObserved.ShouldBeTrue();
    }

    private static async Task<JobScheduleResponse> CreateScheduleAsync(
        JobScheduleService service,
        InMemoryJobScheduleStore store,
        DateTime now)
    {
        var created = await service.CreateAsync(new CreateJobScheduleRequest
        {
            Name = $"lease-test-{Guid.NewGuid():N}",
            JobType = "test-job",
            CronExpression = "*/5 * * * *",
            TimeZoneId = "UTC"
        });
        var schedule = await store.GetScheduleByIdAsync(created.Id);
        schedule!.NextRunUtc = now.AddMinutes(-1);
        (await store.UpdateScheduleAsync(schedule)).ShouldBeTrue();
        return created;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(1);
        }

        throw new TimeoutException("Condition was not reached.");
    }

    private static JobCommandDispatcher CreateDispatcher(IMcpDocService? mcpDocService = null)
    {
        var indexerClient = new RecordingIndexerClient();
        return new JobCommandDispatcher(
            new DiscoverRepositoriesJob(indexerClient, NullLogger<DiscoverRepositoriesJob>.Instance),
            new ReIndexAllRepositoriesJob(indexerClient),
            new ProcessBatchAnalysisJob(indexerClient),
            new LinkAndDetectJob(indexerClient),
            new DetectCommunitiesJob(indexerClient),
            new RegenerateMcpDocsJob(mcpDocService ?? new RecordingMcpDocService()),
            new AssistantRetentionCleanupJob(new RecordingAssistantRetentionCleanupService()),
            new IngestConventionEmbeddingsJob(new FakeConventionEmbeddingService()));
    }

    private sealed class FakeConventionEmbeddingService : IConventionEmbeddingService
    {
        public Task<int> IngestAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ReindexPageAsync(long pageId, bool deleted, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ConventionSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConventionSearchResult>>([]);
    }

    private sealed class BlockingDispatcher(ManualJobScheduleClock clock) : IJobCommandDispatcher
    {
        private readonly TaskCompletionSource<JobExecutionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public IReadOnlyList<string> GetSupportedJobTypes() => ["test-job"];
        public string NormalizeArgsJson(string jobType, JsonElement? args) => "{}";

        public async Task<JobExecutionResult> ExecuteAsync(
            string jobType,
            string argsJson,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            try
            {
                return await _completion.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public void Complete() => _completion.TrySetResult(new JobExecutionResult(
            true,
            "completed",
            clock.UtcNow,
            clock.UtcNow));
    }

    private sealed class StaleCompletionDispatcher(DateTime completedAtUtc) : IJobCommandDispatcher
    {
        private readonly TaskCompletionSource<JobExecutionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> GetSupportedJobTypes() => ["test-job"];
        public string NormalizeArgsJson(string jobType, JsonElement? args) => "{}";

        public async Task<JobExecutionResult> ExecuteAsync(
            string jobType,
            string argsJson,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            return await _completion.Task.WaitAsync(ct);
        }

        public void Complete() => _completion.TrySetResult(new JobExecutionResult(
            true,
            "completed",
            completedAtUtc.AddMinutes(-1),
            completedAtUtc));
    }

    private sealed class BlockingMcpDocService : IMcpDocService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async Task RegenerateAsync(CancellationToken ct = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class ManualJobScheduleClock(DateTime utcNow) : IJobScheduleClock
    {
        private readonly object _sync = new();
        private readonly List<(DateTime DueUtc, TaskCompletionSource Completion)> _delays = [];
        private DateTime _utcNow = utcNow;

        public DateTime UtcNow
        {
            get
            {
                lock (_sync)
                    return _utcNow;
            }
        }

        public int PendingDelayCount
        {
            get
            {
                lock (_sync)
                    return _delays.Count(delay => !delay.Completion.Task.IsCompleted);
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            lock (_sync)
            {
                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _delays.Add((_utcNow.Add(delay), completion));
                return completion.Task.WaitAsync(ct);
            }
        }

        public void Advance(TimeSpan duration)
        {
            List<TaskCompletionSource> due;
            lock (_sync)
            {
                _utcNow = _utcNow.Add(duration);
                due = _delays
                    .Where(delay => delay.DueUtc <= _utcNow)
                    .Select(delay => delay.Completion)
                    .ToList();
                _delays.RemoveAll(delay => delay.DueUtc <= _utcNow);
            }

            foreach (var completion in due)
                completion.TrySetResult();
        }

        public void AdvanceWithoutCompletingDelays(TimeSpan duration)
        {
            lock (_sync)
                _utcNow = _utcNow.Add(duration);
        }
    }
}
