using Anthropic.Models.Messages;
using CodeGraph.Services.Assistant;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class GraphAssistantTelemetryTests
{
    [Fact]
    public void BuildAnthropicUsageRecord_FoldsCacheTokensIntoInputTokens()
    {
        var record = GraphAssistant.BuildAnthropicUsageRecord(
            new MessageDeltaUsage
            {
                InputTokens = 10,
                CacheCreationInputTokens = 3,
                CacheReadInputTokens = 7,
                OutputTokens = 20,
                ServerToolUse = new ServerToolUsage { WebSearchRequests = 0 }
            },
            " Michael ",
            "assistant.ask",
            "claude-sonnet-4-5");

        record.ShouldNotBeNull();
        record.Username.ShouldBe("michael");
        record.Path.ShouldBe("assistant.ask");
        record.Provider.ShouldBe("anthropic");
        record.Model.ShouldBe("claude-sonnet-4-5");
        record.InputTokens.ShouldBe(20);
        record.OutputTokens.ShouldBe(20);
        record.TotalTokens.ShouldBe(40);
    }

    [Fact]
    public void BuildAnthropicUsageRecord_ReturnsNullWhenNoUsageWasReported()
    {
        GraphAssistant.BuildAnthropicUsageRecord(null, "michael", "assistant.ask", "claude")
            .ShouldBeNull();

        GraphAssistant.BuildAnthropicUsageRecord(
            new MessageDeltaUsage
            {
                InputTokens = 0,
                CacheCreationInputTokens = 0,
                CacheReadInputTokens = 0,
                OutputTokens = 0,
                ServerToolUse = new ServerToolUsage { WebSearchRequests = 0 }
            },
            "michael",
            "assistant.ask",
            "claude")
            .ShouldBeNull();
    }

    [Fact]
    public void BuildAnthropicUsageRecord_ClampsLargeTokenCounts()
    {
        var record = GraphAssistant.BuildAnthropicUsageRecord(
            new MessageDeltaUsage
            {
                InputTokens = long.MaxValue,
                CacheCreationInputTokens = 0,
                CacheReadInputTokens = 0,
                OutputTokens = long.MaxValue,
                ServerToolUse = new ServerToolUsage { WebSearchRequests = 0 }
            },
            null,
            "assistant.ask",
            "");

        record.ShouldNotBeNull();
        record.Username.ShouldBe("system");
        record.Model.ShouldBe("unknown");
        record.InputTokens.ShouldBe(int.MaxValue);
        record.OutputTokens.ShouldBe(int.MaxValue);
        record.TotalTokens.ShouldBe(int.MaxValue);
    }
}
