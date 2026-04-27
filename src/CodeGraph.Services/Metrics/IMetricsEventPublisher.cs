using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;

namespace CodeGraph.Services.Metrics;

public interface IMetricsEventPublisher
{
    Task<LlmUsageRecord> PublishLlmUsageAsync(LlmUsageRecord usage, CancellationToken ct = default);
    Task<IReadOnlyList<LlmUsageRecord>> PublishLlmUsageBatchAsync(IEnumerable<LlmUsageRecord> usage, CancellationToken ct = default);
    Task<McpToolInvocationRecord> PublishMcpToolInvocationAsync(McpToolInvocationRecord invocation, CancellationToken ct = default);
}
