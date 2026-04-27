using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Services;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class AdminReportsServiceTests
{
    [Fact]
    public async Task GetAssistantUsageAsync_CombinesTokenUsageAndRunCounts()
    {
        var now = new DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc);
        var store = new RecordingAdminReportsStore();
        store.Usage.AddRange([
            new LlmUsageEntity
            {
                Username = "michael",
                Path = "Assistant",
                Provider = "openai",
                Model = "gpt-5",
                InputTokens = 10,
                OutputTokens = 15,
                TotalTokens = 25,
                CreatedAt = now.AddHours(-2)
            },
            new LlmUsageEntity
            {
                Username = "michael",
                Path = "CodeReview",
                Provider = "openai",
                Model = "gpt-5",
                TotalTokens = 100,
                CreatedAt = now.AddHours(-2)
            }
        ]);
        store.Runs.Add(new AssistantRunEntity
        {
            Username = "michael",
            ChatId = "chat-1",
            Status = "completed",
            ProviderUsed = "openai",
            ModelUsed = "gpt-5",
            CreatedAt = now.AddHours(-1)
        });

        var service = new AdminReportsService(store, new FixedTimeProvider(now));
        var report = await service.GetAssistantUsageAsync(new AdminReportQueryRequest
        {
            Start = now.AddDays(-1),
            End = now
        });

        report.Totals.Single(total => total.Key == "totalTokens").Value.ShouldBe(25);
        report.Totals.Single(total => total.Key == "runCount").Value.ShouldBe(1);
        report.Breakdowns.ShouldContain(item => item.Dimension == "provider" && item.Key == "openai" && item.Value == 25);
    }

    [Fact]
    public async Task GetMcpUsageAsync_ReturnsToolAndStatusBreakdowns()
    {
        var now = new DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc);
        var store = new RecordingAdminReportsStore();
        store.Invocations.AddRange([
            new McpToolInvocationEntity
            {
                Username = "michael",
                ToolName = "search_graph",
                Success = true,
                DurationMs = 20,
                CreatedAt = now.AddHours(-1)
            },
            new McpToolInvocationEntity
            {
                Username = "michael",
                ToolName = "search_graph",
                Success = false,
                DurationMs = 40,
                CreatedAt = now.AddMinutes(-30)
            }
        ]);

        var service = new AdminReportsService(store, new FixedTimeProvider(now));
        var report = await service.GetMcpUsageAsync(new AdminReportQueryRequest
        {
            Start = now.AddDays(-1),
            End = now
        });

        report.Totals.Single(total => total.Key == "callCount").Value.ShouldBe(2);
        report.Totals.Single(total => total.Key == "averageDurationMs").Value.ShouldBe(30);
        report.Breakdowns.ShouldContain(item => item.Dimension == "status" && item.Key == "success" && item.Value == 1);
        report.Breakdowns.ShouldContain(item => item.Dimension == "status" && item.Key == "failure" && item.Value == 1);
    }

    private sealed class RecordingAdminReportsStore : IAdminReportsStore
    {
        public List<LlmUsageEntity> Usage { get; } = [];
        public List<AssistantRunEntity> Runs { get; } = [];
        public List<McpToolInvocationEntity> Invocations { get; } = [];

        public Task<IReadOnlyList<LlmUsageEntity>> GetLlmUsageAsync(DateTime start, DateTime end, string? path = null, string? username = null, string? provider = null, string? model = null, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<LlmUsageEntity>>(Usage
                .Where(row => row.CreatedAt >= start && row.CreatedAt < end)
                .Where(row => path is null || row.Path == path)
                .Where(row => username is null || row.Username == username)
                .Where(row => provider is null || row.Provider == provider)
                .Where(row => model is null || row.Model == model)
                .ToList());
        }

        public Task<IReadOnlyList<AssistantRunEntity>> GetAssistantRunsAsync(DateTime start, DateTime end, string? username = null, string? provider = null, string? model = null, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<AssistantRunEntity>>(Runs
                .Where(row => row.CreatedAt >= start && row.CreatedAt < end)
                .Where(row => username is null || row.Username == username)
                .Where(row => provider is null || row.ProviderUsed == provider)
                .Where(row => model is null || row.ModelUsed == model)
                .ToList());
        }

        public Task<IReadOnlyList<McpToolInvocationEntity>> GetMcpToolInvocationsAsync(DateTime start, DateTime end, string? username = null, string? tool = null, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<McpToolInvocationEntity>>(Invocations
                .Where(row => row.CreatedAt >= start && row.CreatedAt < end)
                .Where(row => username is null || row.Username == username)
                .Where(row => tool is null || row.ToolName == tool)
                .ToList());
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
