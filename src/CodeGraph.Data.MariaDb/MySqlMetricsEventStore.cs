using CodeGraph.Data;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlMetricsEventStore(IOptions<MariaDbStorageOptions> optionsAccessor) : IMetricsEventStore
{
    private readonly MariaDbStorageOptions options = optionsAccessor.Value;

    static MySqlMetricsEventStore()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task CreateLlmUsageAsync(LlmUsageEntity usage)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            """
            INSERT IGNORE INTO llm_usage
                (event_id, username, path, provider, model, input_tokens, output_tokens, total_tokens, created_at)
            VALUES
                (COALESCE(NULLIF(@EventId, ''), REPLACE(UUID(), '-', '')),
                 @Username, @Path, @Provider, @Model, @InputTokens, @OutputTokens, @TotalTokens, @CreatedAt)
            """,
            usage);
    }

    public async Task CreateLlmUsageBatchAsync(IEnumerable<LlmUsageEntity> usage)
    {
        var items = usage.ToList();
        if (items.Count == 0)
        {
            return;
        }

        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            """
            INSERT IGNORE INTO llm_usage
                (event_id, username, path, provider, model, input_tokens, output_tokens, total_tokens, created_at)
            VALUES
                (COALESCE(NULLIF(@EventId, ''), REPLACE(UUID(), '-', '')),
                 @Username, @Path, @Provider, @Model, @InputTokens, @OutputTokens, @TotalTokens, @CreatedAt)
            """,
            items);
    }

    public async Task CreateMcpToolInvocationAsync(McpToolInvocationEntity invocation)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            """
            INSERT IGNORE INTO mcp_tool_invocations
                (event_id, username, token_id, tool_name, success, duration_ms, error_code, created_at)
            VALUES
                (COALESCE(NULLIF(@EventId, ''), REPLACE(UUID(), '-', '')),
                 @Username, @TokenId, @ToolName, @Success, @DurationMs, @ErrorCode, @CreatedAt)
            """,
            invocation);
    }

    private async Task<MySqlConnection> GetOpenConnectionAsync()
    {
        var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
