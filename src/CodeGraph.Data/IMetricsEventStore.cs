namespace CodeGraph.Data;

public interface IMetricsEventStore
{
    Task CreateLlmUsageAsync(LlmUsageEntity usage);
    Task CreateLlmUsageBatchAsync(IEnumerable<LlmUsageEntity> usage);
    Task CreateMcpToolInvocationAsync(McpToolInvocationEntity invocation);
}
