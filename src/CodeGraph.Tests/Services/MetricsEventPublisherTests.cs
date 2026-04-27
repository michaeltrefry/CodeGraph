using CodeGraph.Models.Messages;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class MetricsEventPublisherTests
{
    [Fact]
    public async Task PublishLlmUsageAsync_NormalizesAndPublishesUsage()
    {
        var bus = new RecordingMessageBus();
        var publisher = new MetricsEventPublisher(bus, NullLogger<MetricsEventPublisher>.Instance);

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
        record.EventId.ShouldNotBeNullOrWhiteSpace();
        var message = bus.Published.OfType<LlmUsageRecorded>().Single();
        message.EventId.ShouldBe(record.EventId);
        message.Username.ShouldBe("michael");
        message.Path.ShouldBe("Assistant");
        message.TotalTokens.ShouldBe(7);
    }

    [Fact]
    public async Task PublishMcpToolInvocationAsync_NormalizesAndPublishesInvocation()
    {
        var bus = new RecordingMessageBus();
        var publisher = new MetricsEventPublisher(bus, NullLogger<MetricsEventPublisher>.Instance);

        var record = await publisher.PublishMcpToolInvocationAsync(new McpToolInvocationRecord(
            " search_graph ",
            false,
            -12,
            " Michael ",
            ErrorCode: new string('x', 300)));

        record.Username.ShouldBe("michael");
        record.DurationMs.ShouldBe(0);
        record.ErrorCode!.Length.ShouldBe(255);
        record.EventId.ShouldNotBeNullOrWhiteSpace();
        var message = bus.Published.OfType<McpToolInvocationRecorded>().Single();
        message.EventId.ShouldBe(record.EventId);
        message.ToolName.ShouldBe("search_graph");
        message.Success.ShouldBeFalse();
        message.DurationMs.ShouldBe(0);
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object> Published { get; } = [];

        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            Published.Add(message);
            return Task.CompletedTask;
        }
    }
}
