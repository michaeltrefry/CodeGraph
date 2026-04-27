using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;

namespace CodeGraph.Services.Metrics;

public interface IMetricsEventRecorder
{
    Task<LlmUsageRecord> RecordLlmUsageAsync(LlmUsageRecord usage, CancellationToken ct = default);
    Task<IReadOnlyList<LlmUsageRecord>> RecordLlmUsageBatchAsync(IEnumerable<LlmUsageRecord> usage, CancellationToken ct = default);
    Task<McpToolInvocationRecord> RecordMcpToolInvocationAsync(McpToolInvocationRecord invocation, CancellationToken ct = default);
}
