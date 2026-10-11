using CodeGraph.Host.Shared.Consumers;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Pipeline;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeGraph.Tests.HostShared;

public class ProcessRepositoryConsumerTests
{
    [Fact]
    public async Task Consume_LogsFailureAtErrorWithRepositoryPhaseAndException()
    {
        var failure = new TimeoutException("graph write timed out");
        IndexingFailure.TagPhase(failure, "graph-write");

        var entry = await ConsumeFailingAsync(failure);

        entry.Exception.ShouldBeSameAs(failure);
        entry.Properties["Repo"].ShouldBe("inference");
        entry.Properties["Phase"].ShouldBe("graph-write");
        entry.Properties["Cancelled"].ShouldBe("");
    }

    [Fact]
    public async Task Consume_LogsCancellationAtError()
    {
        var failure = new OperationCanceledException("consume cancelled");

        var entry = await ConsumeFailingAsync(failure);

        entry.Exception.ShouldBeSameAs(failure);
        entry.Properties["Repo"].ShouldBe("inference");
        entry.Properties["Phase"].ShouldBe("unknown");
        entry.Properties["Cancelled"].ShouldBe(" (cancelled)");
    }

    private static async Task<RecordingLogger.Entry> ConsumeFailingAsync(Exception failure)
    {
        var logger = new RecordingLogger();
        await using var provider = new ServiceCollection()
            .AddSingleton<IProjectService>(new ThrowingProjectService(failure))
            .AddSingleton<ILogger<ProcessRepositoryConsumer>>(logger)
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<ProcessRepositoryConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new ProcessRepository { Name = "inference", ShouldIndex = true });
            (await harness.GetConsumerHarness<ProcessRepositoryConsumer>().Consumed
                .Any<ProcessRepository>(context => context.Exception is not null)).ShouldBeTrue();
        }
        finally
        {
            await harness.Stop();
        }

        return logger.Entries.ShouldHaveSingleItem();
    }

    private sealed class ThrowingProjectService(Exception failure) : IProjectService
    {
        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new()) =>
            throw new NotSupportedException();

        public Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new()) =>
            Task.FromException(failure);

        public Task<bool> DeleteRepositoryAsync(string repo) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<ProcessRepositoryConsumer>
    {
        public sealed record Entry(Exception? Exception, IReadOnlyDictionary<string, object?> Properties);

        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_entries) return _entries.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Error) return;
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            lock (_entries) _entries.Add(new Entry(exception, properties));
        }
    }
}
