namespace CodeGraph.Data;

public interface IAdminReportsStore
{
    Task<IReadOnlyList<LlmUsageEntity>> GetLlmUsageAsync(
        DateTime start,
        DateTime end,
        string? path = null,
        string? username = null,
        string? provider = null,
        string? model = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<AssistantRunEntity>> GetAssistantRunsAsync(
        DateTime start,
        DateTime end,
        string? username = null,
        string? provider = null,
        string? model = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<McpToolInvocationEntity>> GetMcpToolInvocationsAsync(
        DateTime start,
        DateTime end,
        string? username = null,
        string? tool = null,
        CancellationToken ct = default);
}
