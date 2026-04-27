using CodeGraph.Data;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class MetricsEventRecorderTests
{
    [Fact]
    public async Task RecordLlmUsageAsync_NormalizesAndPersistsUsage()
    {
        var store = new RecordingMetricsEventStore();
        var recorder = new MetricsEventRecorder(store, NullLogger<MetricsEventRecorder>.Instance);

        var record = await recorder.RecordLlmUsageAsync(new LlmUsageRecord(
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
        record.EventId.ShouldNotBeNullOrWhiteSpace();
        var entity = store.Usage.Single();
        entity.EventId.ShouldBe(record.EventId);
        entity.Username.ShouldBe("michael");
        entity.Path.ShouldBe("Assistant");
        entity.TotalTokens.ShouldBe(7);
    }

    [Fact]
    public async Task RecordMcpToolInvocationAsync_NormalizesAndPersistsInvocation()
    {
        var store = new RecordingMetricsEventStore();
        var recorder = new MetricsEventRecorder(store, NullLogger<MetricsEventRecorder>.Instance);

        var record = await recorder.RecordMcpToolInvocationAsync(new McpToolInvocationRecord(
            " search_graph ",
            false,
            -12,
            " Michael ",
            ErrorCode: new string('x', 300)));

        record.Username.ShouldBe("michael");
        record.DurationMs.ShouldBe(0);
        record.ErrorCode!.Length.ShouldBe(255);
        record.EventId.ShouldNotBeNullOrWhiteSpace();
        var entity = store.Invocations.Single();
        entity.EventId.ShouldBe(record.EventId);
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
