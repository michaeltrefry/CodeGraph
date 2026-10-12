using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using static CodeGraph.Tests.Services.StandaloneIndexerOperationsServiceTests;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-25698: ProcessRepository runs execute as durable indexer runs under a heartbeated lease
/// instead of under a held RabbitMQ delivery.
/// </summary>
public class ProcessRepositoryDurableRunTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LongRun_HeartbeatRenewsTheLeaseForTwoHoursAndIsNeverReclaimed()
    {
        var time = new ManualTimeProvider();
        var runs = new FakeIndexerRunStore { EnforceLeaseExpiry = true };
        var projects = new BlockingProjectService();
        var runId = (await Handoff(runs).HandOffAsync(Routine())).RunId;
        using var services = CreateServices(runs, projects);
        var worker = CreateWorker(services, time, new IndexerRunWorkerOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(45),
            HeartbeatInterval = TimeSpan.FromSeconds(10)
        });

        var execution = worker.TryExecuteNextAsync(CancellationToken.None, IndexerRunBackgroundRunner.RepositoryLane);
        await projects.Started.Task.WaitAsync(WaitLimit);

        // Two hours of virtual time, one heartbeat interval at a time.
        for (var beat = 0; beat < 720; beat++)
        {
            await WaitUntilAsync(() => time.PendingTimers > 0);
            time.Advance(TimeSpan.FromSeconds(10));
            var now = time.GetUtcNow().UtcDateTime;
            await WaitUntilAsync(() => runs.GetIndexerRunAsync(runId).Result!.HeartbeatAt == now);

            // A restarted or second worker polling now finds nothing to recover.
            (await runs.TryClaimNextIndexerRunAsync(
                    "other-worker", now, now.AddSeconds(45), 3, IndexerRunBackgroundRunner.RepositoryLane))
                .ShouldBeNull();
        }

        projects.Finish.SetResult();
        (await execution.WaitAsync(WaitLimit)).ShouldBeTrue();
        var completed = (await runs.GetIndexerRunAsync(runId))!;
        completed.Status.ShouldBe("completed");
        completed.AttemptCount.ShouldBe(1);
        projects.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task LongRun_SurvivesATransientHeartbeatFailureWhileTheLeaseIsStillLive()
    {
        var time = new ManualTimeProvider();
        var runs = new FakeIndexerRunStore { EnforceLeaseExpiry = true, FailNextRenewals = 1 };
        var projects = new BlockingProjectService();
        var runId = (await Handoff(runs).HandOffAsync(Routine())).RunId;
        using var services = CreateServices(runs, projects);
        var worker = CreateWorker(services, time, new IndexerRunWorkerOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(45),
            HeartbeatInterval = TimeSpan.FromSeconds(10)
        });

        var execution = worker.TryExecuteNextAsync(CancellationToken.None, IndexerRunBackgroundRunner.RepositoryLane);
        await projects.Started.Task.WaitAsync(WaitLimit);

        // The first heartbeat fails; the next one renews the still-live lease.
        await WaitUntilAsync(() => time.PendingTimers > 0);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => runs.FailNextRenewals == 0 && time.PendingTimers > 0);
        time.Advance(TimeSpan.FromSeconds(10));
        var now = time.GetUtcNow().UtcDateTime;
        await WaitUntilAsync(() => runs.GetIndexerRunAsync(runId).Result!.HeartbeatAt == now);

        projects.Finish.SetResult();
        (await execution.WaitAsync(WaitLimit)).ShouldBeTrue();
        (await runs.GetIndexerRunAsync(runId))!.Status.ShouldBe("completed");
    }

    [Fact]
    public async Task RepositoryRuns_HaveTheirOwnLaneAndDoNotBlockOtherIndexerWork()
    {
        var runs = new FakeIndexerRunStore();
        var projects = new BlockingProjectService();
        var repoRun = (await Handoff(runs).HandOffAsync(Routine())).RunId;
        var linkRun = await runs.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.Link,
            Target = "all",
            Status = "queued",
            RetrySafe = true,
            CreatedAt = DateTime.UtcNow.AddSeconds(1)
        });
        using var services = CreateServices(runs, projects);
        var worker = CreateWorker(services, TimeProvider.System, new IndexerRunWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            LeaseDuration = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(1)
        });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await projects.Started.Task.WaitAsync(WaitLimit);
            await WaitUntilAsync(() => runs.GetIndexerRunAsync(linkRun).Result!.Status == "completed");
            (await runs.GetIndexerRunAsync(repoRun))!.Status.ShouldBe("running");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("graph-write", "failed", 1)]
    [InlineData("resolution", "failed", 1)]
    [InlineData("resolve-repository", "queued", 1)]
    [InlineData("check-up-to-date", "queued", 1)]
    public async Task FailedRepositoryRun_IsOnlyRetriedForPreIndexPhases(string phase, string status, int attempts)
    {
        var runs = new FakeIndexerRunStore();
        var failure = new InvalidOperationException($"failed in {phase}");
        IndexingFailure.TagPhase(failure, phase);
        var runId = (await Handoff(runs).HandOffAsync(Routine())).RunId;
        using var services = CreateServices(runs, new FailingProjectService(failure));
        var worker = CreateWorker(services, TimeProvider.System, new IndexerRunWorkerOptions());

        (await worker.TryExecuteNextAsync(CancellationToken.None, IndexerRunBackgroundRunner.RepositoryLane))
            .ShouldBeTrue();

        var run = (await runs.GetIndexerRunAsync(runId))!;
        run.Status.ShouldBe(status);
        run.AttemptCount.ShouldBe(attempts);
        run.Error.ShouldBe($"[{phase}] System.InvalidOperationException: failed in {phase}");
    }

    [Fact]
    public async Task DurableRun_PassesTheWholeProcessRepositoryMessageToTheProjectService()
    {
        var runs = new FakeIndexerRunStore();
        var projects = new BlockingProjectService();
        projects.Finish.SetResult();
        var message = new ProcessRepository
        {
            Name = "SceneWorks",
            Path = "/repos/SceneWorks",
            RepoUrl = "https://example.test/SceneWorks.git",
            SourceGroup = "factory",
            ShouldIndex = true,
            ShouldAnalyze = true,
            SkipIfUpToDate = true,
            IncludeAllSource = true,
            ReplaceExistingGraph = true,
            ShouldComputeVitals = false
        };
        var runId = (await Handoff(runs).HandOffAsync(message)).RunId;
        using var services = CreateServices(runs, projects);
        var worker = CreateWorker(services, TimeProvider.System, new IndexerRunWorkerOptions());

        (await worker.TryExecuteNextAsync(CancellationToken.None, IndexerRunBackgroundRunner.RepositoryLane))
            .ShouldBeTrue();

        projects.LastMessage.ShouldBeEquivalentTo(message);
        (await runs.GetIndexerRunAsync(runId))!.Status.ShouldBe("completed");
    }

    private static ProcessRepository Routine() =>
        new() { Name = "SceneWorks", ShouldIndex = true, ShouldAnalyze = true, SkipIfUpToDate = true };

    private static ProcessRepositoryHandoff Handoff(FakeIndexerRunStore runs) =>
        new(runs, new RecordingBackgroundRunner(), NullLogger<ProcessRepositoryHandoff>.Instance);

    private static ServiceProvider CreateServices(FakeIndexerRunStore runs, IProjectService projects) =>
        CreateWorkerServices(
            runs, new FakeDatabaseSourceStore(), new RecordingDatabaseSchemaExtractor(), new RecordingAdminService(),
            projects);

    private static IndexerRunBackgroundRunner CreateWorker(
        ServiceProvider services, TimeProvider time, IndexerRunWorkerOptions options) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<IndexerRunBackgroundRunner>.Instance,
            Options.Create(new IndexingOptions { MaxParallelRepos = 1 }),
            time);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached.");
            await Task.Delay(1);
        }
    }

    private sealed class BlockingProjectService : IProjectService
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProcessRepository? LastMessage { get; private set; }

        public async Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new())
        {
            Interlocked.Increment(ref _calls);
            LastMessage = message;
            Started.TrySetResult();
            await Finish.Task.WaitAsync(cancellationToken);
        }

        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new()) =>
            throw new NotSupportedException();

        public Task<bool> DeleteRepositoryAsync(string repo) => throw new NotSupportedException();
    }

    private sealed class FailingProjectService(Exception failure) : IProjectService
    {
        public Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new()) =>
            Task.FromException(failure);

        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new()) =>
            throw new NotSupportedException();

        public Task<bool> DeleteRepositoryAsync(string repo) => throw new NotSupportedException();
    }
}
