using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Host.Shared.Consumers;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Indexer;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeGraph.Tests.Services.StandaloneIndexerOperationsServiceTests;

namespace CodeGraph.Tests.HostShared;

/// <summary>
/// sc-25698: indexing must never run while a RabbitMQ delivery is held (consumer_timeout loop).
/// The consumer records one durable indexer run and acks.
/// </summary>
public class ProcessRepositoryConsumerTests
{
    [Fact]
    public async Task Consume_AcksAfterQueuingOneDurableRunWithoutRunningTheIndex()
    {
        var runs = new FakeIndexerRunStore();
        var runner = new RecordingBackgroundRunner();
        var repositories = new NeverCompletingRepositoryService();
        await using var provider = new ServiceCollection()
            .AddSingleton<IIndexerRunStore>(runs)
            .AddSingleton<IIndexerRunBackgroundRunner>(runner)
            .AddSingleton<IRepositoryService>(repositories)
            .AddSingleton<ILogger<ProcessRepositoryHandoff>>(NullLogger<ProcessRepositoryHandoff>.Instance)
            .AddTransient<ProcessRepositoryHandoff>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<ProcessRepositoryConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();
        try
        {
            var message = new ProcessRepository
            {
                Name = "SceneWorks", ShouldIndex = true, ShouldAnalyze = true, SkipIfUpToDate = true
            };
            await harness.Bus.Publish(message);

            (await harness.GetConsumerHarness<ProcessRepositoryConsumer>().Consumed
                .Any<ProcessRepository>(context => context.Exception is null)).ShouldBeTrue();
            repositories.Calls.ShouldBe(0);

            var run = (await runs.ListIndexerRunsAsync()).ShouldHaveSingleItem();
            run.Operation.ShouldBe(IndexerRunOperations.ProcessRepository);
            run.Target.ShouldBe("SceneWorks");
            run.Status.ShouldBe("queued");
            run.RetrySafe.ShouldBeTrue();
            var queued = JsonSerializer.Deserialize<ProcessRepository>(
                run.ArgsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            queued.ShouldBeEquivalentTo(message);
            runner.EnqueuedRunIds.ShouldBe([run.Id]);
        }
        finally
        {
            repositories.Release.TrySetResult();
            await harness.Stop();
        }
    }

    [Fact]
    public async Task HandOff_CoalescesIdenticalMessagesWhileTheirRunIsActive()
    {
        var runs = new FakeIndexerRunStore();
        var runner = new RecordingBackgroundRunner();
        var logger = new RecordingLogger();
        var handoff = new ProcessRepositoryHandoff(runs, runner, logger);
        var routine = new ProcessRepository { Name = "SceneWorks", ShouldIndex = true, SkipIfUpToDate = true };

        var first = await handoff.HandOffAsync(routine);
        var redelivered = await handoff.HandOffAsync(routine);
        first.Created.ShouldBeTrue();
        redelivered.Created.ShouldBeFalse();
        redelivered.RunId.ShouldBe(first.RunId);

        // Still coalesced while that run is executing.
        runs.SetStatus(first.RunId, "running");
        (await handoff.HandOffAsync(routine)).RunId.ShouldBe(first.RunId);

        // Different work for the same repository (a manual reindex) is not dropped.
        var manual = await handoff.HandOffAsync(new ProcessRepository
        {
            Name = "SceneWorks", ShouldIndex = true, SkipIfUpToDate = false
        });
        manual.Created.ShouldBeTrue();

        // Once the run is terminal, the next sync queues a new run.
        runs.SetStatus(first.RunId, "completed");
        var next = await handoff.HandOffAsync(routine);
        next.Created.ShouldBeTrue();

        runner.EnqueuedRunIds.ShouldBe([first.RunId, manual.RunId, next.RunId]);
        (await runs.ListIndexerRunsAsync()).Count.ShouldBe(3);
        logger.Messages.Count(text => text.StartsWith("Handed off ProcessRepository for SceneWorks")).ShouldBe(3);
        logger.Messages.Count(text => text.StartsWith("Coalesced ProcessRepository for SceneWorks")).ShouldBe(2);
    }

    private sealed class NeverCompletingRepositoryService : IRepositoryService
    {
        private int _calls;
        public int Calls => _calls;

        /// <summary>Stands in for an index that outlasts the test (and the broker's consumer_timeout).</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new()) =>
            throw new NotSupportedException();

        public Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new())
        {
            Interlocked.Increment(ref _calls);
            return Release.Task;
        }

        public Task<bool> DeleteRepositoryAsync(string repo) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<ProcessRepositoryHandoff>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
                Messages.Add(formatter(state, exception));
        }
    }
}
