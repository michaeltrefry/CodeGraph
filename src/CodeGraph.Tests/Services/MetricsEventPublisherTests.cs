using CodeGraph.Data;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class MetricsEventPublisherTests
{
    [Fact]
    public async Task PublishLlmUsageAsync_NormalizesAndPersistsUsage()
    {
        var store = new RecordingMetricsEventStore();
        var publisher = new MetricsEventPublisher(store, NullLogger<MetricsEventPublisher>.Instance);

        var record = await publisher.PublishLlmUsageAsync(new LlmUsageRecord(
            " Michael ",
            "Assistant",
            " openai ",
            " gpt-5 ",
            -1,
            7,
            0));

        record.Username.ShouldBe("michael");
        record.InputTokens.ShouldBe(0);
        record.TotalTokens.ShouldBe(7);
        var entity = store.Usage.Single();
        entity.Username.ShouldBe("michael");
        entity.Path.ShouldBe("Assistant");
        entity.TotalTokens.ShouldBe(7);
        entity.EventId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PublishMcpToolInvocationAsync_NormalizesAndPersistsInvocation()
    {
        var store = new RecordingMetricsEventStore();
        var publisher = new MetricsEventPublisher(store, NullLogger<MetricsEventPublisher>.Instance);

        var record = await publisher.PublishMcpToolInvocationAsync(new McpToolInvocationRecord(
            " search_graph ",
            false,
            -12,
            " Michael ",
            ErrorCode: new string('x', 300)));

        record.Username.ShouldBe("michael");
        record.DurationMs.ShouldBe(0);
        record.ErrorCode!.Length.ShouldBe(255);
        var entity = store.Invocations.Single();
        entity.ToolName.ShouldBe("search_graph");
        entity.Success.ShouldBeFalse();
        entity.DurationMs.ShouldBe(0);
    }

    private sealed class RecordingMetricsEventStore : IMetricsEventStore
    {
        public List<LlmUsageEntity> Usage { get; } = [];
        public List<McpToolInvocationEntity> Invocations { get; } = [];

        public Task CreateLlmUsageAsync(LlmUsageEntity usage)
        {
            Usage.Add(usage);
            return Task.CompletedTask;
        }

        public Task CreateLlmUsageBatchAsync(IEnumerable<LlmUsageEntity> usage)
        {
            Usage.AddRange(usage);
            return Task.CompletedTask;
        }

        public Task CreateMcpToolInvocationAsync(McpToolInvocationEntity invocation)
        {
            Invocations.Add(invocation);
            return Task.CompletedTask;
        }
    }
}
