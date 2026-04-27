using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlAdminReportsStore(CodeGraphDbContext db) : IAdminReportsStore
{
    public async Task<IReadOnlyList<LlmUsageEntity>> GetLlmUsageAsync(
        DateTime start,
        DateTime end,
        string? path = null,
        string? username = null,
        string? provider = null,
        string? model = null,
        CancellationToken ct = default)
    {
        var query = db.LlmUsage
            .AsNoTracking()
            .Where(row => row.CreatedAt >= start && row.CreatedAt < end);

        if (!string.IsNullOrWhiteSpace(path))
            query = query.Where(row => row.Path == path);
        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(row => row.Username == username);
        if (!string.IsNullOrWhiteSpace(provider))
            query = query.Where(row => row.Provider == provider);
        if (!string.IsNullOrWhiteSpace(model))
            query = query.Where(row => row.Model == model);

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AssistantRunEntity>> GetAssistantRunsAsync(
        DateTime start,
        DateTime end,
        string? username = null,
        string? provider = null,
        string? model = null,
        CancellationToken ct = default)
    {
        var query = db.AssistantRuns
            .AsNoTracking()
            .Where(row => row.CreatedAt >= start && row.CreatedAt < end);

        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(row => row.Username == username);
        if (!string.IsNullOrWhiteSpace(provider))
            query = query.Where(row => row.ProviderUsed == provider);
        if (!string.IsNullOrWhiteSpace(model))
            query = query.Where(row => row.ModelUsed == model);

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<McpToolInvocationEntity>> GetMcpToolInvocationsAsync(
        DateTime start,
        DateTime end,
        string? username = null,
        string? tool = null,
        CancellationToken ct = default)
    {
        var query = db.McpToolInvocations
            .AsNoTracking()
            .Where(row => row.CreatedAt >= start && row.CreatedAt < end);

        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(row => row.Username == username);
        if (!string.IsNullOrWhiteSpace(tool))
            query = query.Where(row => row.ToolName == tool);

        return await query.ToListAsync(ct);
    }
}
