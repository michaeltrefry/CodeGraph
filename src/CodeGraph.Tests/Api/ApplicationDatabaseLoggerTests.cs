using CodeGraph.Api.Logging;
using CodeGraph.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Api;

public class ApplicationDatabaseLoggerTests
{
    [Fact]
    public void Logger_QueuesStructuredEntryAtConfiguredLevel()
    {
        var options = new ApplicationDatabaseLoggingOptions
        {
            MinimumLevel = LogLevel.Information
        };
        var channel = new ApplicationLogChannel(Options.Create(options));
        var logger = new ApplicationDatabaseLogger(
            "CodeGraph.Tests.Sample",
            "CodeGraph.Api@test-host",
            channel,
            options,
            () => new LoggerExternalScopeProvider());

        logger.LogWarning(new EventId(42), "Repository {Repository} failed", "CodeGraph");

        channel.Entries.Reader.TryRead(out var entry).ShouldBeTrue();
        entry.ShouldNotBeNull();
        entry.Level.ShouldBe("Warning");
        entry.Category.ShouldBe("CodeGraph.Tests.Sample");
        entry.EventId.ShouldBe(42);
        entry.Message.ShouldBe("Repository CodeGraph failed");
        entry.PropertiesJson!.ShouldContain("Repository");
    }

    [Fact]
    public void Logger_DoesNotQueueEntriesBelowConfiguredLevel()
    {
        var options = new ApplicationDatabaseLoggingOptions
        {
            MinimumLevel = LogLevel.Warning
        };
        var channel = new ApplicationLogChannel(Options.Create(options));
        var logger = new ApplicationDatabaseLogger(
            "CodeGraph.Tests.Sample",
            "CodeGraph.Api@test-host",
            channel,
            options,
            () => new LoggerExternalScopeProvider());

        logger.LogInformation("Not persisted");

        channel.Entries.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void Logger_IgnoresItsOwnAndEntityFrameworkCategories()
    {
        var options = new ApplicationDatabaseLoggingOptions();
        var channel = new ApplicationLogChannel(Options.Create(options));
        var scopeProvider = new LoggerExternalScopeProvider();
        var sinkLogger = new ApplicationDatabaseLogger(
            "CodeGraph.Api.Logging.Writer",
            "source",
            channel,
            options,
            () => scopeProvider);
        var efLogger = new ApplicationDatabaseLogger(
            "Microsoft.EntityFrameworkCore.Database.Command",
            "source",
            channel,
            options,
            () => scopeProvider);

        sinkLogger.LogError("sink failure");
        efLogger.LogWarning("query warning");

        channel.Entries.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task BackgroundWriter_FlushesQueuedLogsAndRunsRetentionWithoutNewEntries()
    {
        var options = new ApplicationDatabaseLoggingOptions
        {
            FlushIntervalMilliseconds = 100,
            RetentionDays = 30
        };
        var channel = new ApplicationLogChannel(Options.Create(options));
        var store = new RecordingApplicationLogStore();
        var services = new ServiceCollection();
        services.AddSingleton<IApplicationLogStore>(store);
        await using var provider = services.BuildServiceProvider();
        var writer = new ApplicationDatabaseLogWriter(
            channel,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options));
        channel.TryWrite(new ApplicationLogEntryEntity
        {
            OccurredAtUtc = DateTime.UtcNow,
            Level = "Error",
            Source = "source",
            Category = "category",
            Message = "message"
        });

        await writer.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => store.Entries.Count == 1 && store.RetentionCutoff.HasValue);
        await writer.StopAsync(CancellationToken.None);

        store.Entries.Single().Message.ShouldBe("message");
        store.RetentionCutoff.ShouldNotBeNull();
        store.RetentionCutoff.Value.ShouldBeLessThan(DateTime.UtcNow.AddDays(-29));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        condition().ShouldBeTrue();
    }

    private sealed class RecordingApplicationLogStore : IApplicationLogStore
    {
        public List<ApplicationLogEntryEntity> Entries { get; } = [];
        public DateTime? RetentionCutoff { get; private set; }

        public Task WriteBatchAsync(IReadOnlyList<ApplicationLogEntryEntity> entries, CancellationToken cancellationToken = default)
        {
            Entries.AddRange(entries);
            return Task.CompletedTask;
        }

        public Task<ApplicationLogPage> QueryAsync(ApplicationLogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationLogPage([], 0));

        public Task<int> DeleteBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        {
            RetentionCutoff = cutoffUtc;
            return Task.FromResult(0);
        }
    }
}
