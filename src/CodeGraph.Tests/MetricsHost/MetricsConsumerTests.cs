using CodeGraph.Metrics.Consumers;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.MetricsHost;

public class MetricsConsumerTests
{
    [Fact]
    public async Task LlmUsageRecordedConsumer_MapsEventIntoRecorderPayload()
    {
        var recorder = new RecordingMetricsEventRecorder();
        var consumer = new LlmUsageRecordedConsumer(
            recorder,
            NullLogger<LlmUsageRecordedConsumer>.Instance);

        await consumer.ProcessAsync(new LlmUsageRecorded
        {
            EventId = "evt_llm_host_001",
            Username = "Michael",
            Path = "Assistant",
            Provider = " openai ",
            Model = " gpt-5.4-mini ",
            InputTokens = 12,
            OutputTokens = 4,
            TotalTokens = 0,
            CreatedAt = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc),
        });

        recorder.LlmUsage.Single().ShouldBe(new LlmUsageRecord(
            "Michael",
            "Assistant",
            " openai ",
            " gpt-5.4-mini ",
            12,
            4,
            0,
            new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc),
            "evt_llm_host_001"));
    }

    [Fact]
    public async Task McpToolInvocationRecordedConsumer_MapsEventIntoRecorderPayload()
    {
        var recorder = new RecordingMetricsEventRecorder();
        var consumer = new McpToolInvocationRecordedConsumer(
            recorder,
            NullLogger<McpToolInvocationRecordedConsumer>.Instance);

        await consumer.ProcessAsync(new McpToolInvocationRecorded
        {
            EventId = "evt_mcp_host_001",
            Username = "Michael",
            TokenId = 42,
            ToolName = " get_graph_schema ",
            Success = false,
            DurationMs = 88,
            ErrorCode = " NullReferenceException ",
            CreatedAt = new DateTime(2026, 4, 14, 13, 0, 0, DateTimeKind.Utc),
        });

        recorder.ToolInvocations.Single().ShouldBe(new McpToolInvocationRecord(
            " get_graph_schema ",
            false,
            88,
            "Michael",
            42,
            " NullReferenceException ",
            new DateTime(2026, 4, 14, 13, 0, 0, DateTimeKind.Utc),
            "evt_mcp_host_001"));
    }

    private sealed class RecordingMetricsEventRecorder : IMetricsEventRecorder
    {
        public List<LlmUsageRecord> LlmUsage { get; } = [];
        public List<McpToolInvocationRecord> ToolInvocations { get; } = [];

        public Task<LlmUsageRecord> RecordLlmUsageAsync(LlmUsageRecord usage, CancellationToken ct = default)
        {
            LlmUsage.Add(usage);
            return Task.FromResult(usage);
        }

        public Task<IReadOnlyList<LlmUsageRecord>> RecordLlmUsageBatchAsync(
            IEnumerable<LlmUsageRecord> usage,
            CancellationToken ct = default)
        {
            var items = usage.ToList();
            LlmUsage.AddRange(items);
            return Task.FromResult<IReadOnlyList<LlmUsageRecord>>(items);
        }

        public Task<McpToolInvocationRecord> RecordMcpToolInvocationAsync(
            McpToolInvocationRecord invocation,
            CancellationToken ct = default)
        {
            ToolInvocations.Add(invocation);
            return Task.FromResult(invocation);
        }
    }
}
