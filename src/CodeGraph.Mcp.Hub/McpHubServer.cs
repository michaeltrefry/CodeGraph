using System.ComponentModel;
using System.Diagnostics;
using System.Security.Claims;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;

namespace CodeGraph.Mcp.Hub;

[McpServerToolType]
public sealed class McpHubServer(McpHubService hub, IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "mcp_hub_catalog", Title = "MCP Hub Catalog", ReadOnly = true)]
    [Description("List MCP Hub providers and tools, including enabled state and entitlement metadata.")]
    public async Task<string> Catalog(CancellationToken cancellationToken)
    {
        var catalog = await hub.GetCatalogAsync(cancellationToken);
        return System.Text.Json.JsonSerializer.Serialize(catalog, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
    }

    [McpServerTool(Name = "shortcut_search_epics", Title = "Search Shortcut Epics", ReadOnly = true)]
    [Description("Search Shortcut epics through your connected (delegated) Shortcut provider credential.")]
    public async Task<string> SearchShortcutEpics(
        [Description("Optional Shortcut search query.")] string? query = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("shortcut", "shortcut_search_epics", "search", "epics", "delegated", () => hub.SearchShortcutEpicsAsync(query, Username, cancellationToken), cancellationToken);

    [McpServerTool(Name = "shortcut_search_stories", Title = "Search Shortcut Stories", ReadOnly = true)]
    [Description("Search Shortcut stories through your connected (delegated) Shortcut provider credential.")]
    public async Task<string> SearchShortcutStories(
        [Description("Optional Shortcut search query.")] string? query = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("shortcut", "shortcut_search_stories", "search", "stories", "delegated", () => hub.SearchShortcutStoriesAsync(query, Username, cancellationToken), cancellationToken);

    [McpServerTool(Name = "rabbitmq_list_queues", Title = "List RabbitMQ Queues", ReadOnly = true)]
    [Description("List RabbitMQ queues through the configured management API.")]
    public async Task<string> ListRabbitMqQueues(
        [Description("RabbitMQ virtual host. All-vhost listing is not allowed.")] string? virtualHost = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("rabbitmq", "rabbitmq_list_queues", "list", virtualHost, "shared", () => hub.ListRabbitMqQueuesAsync(virtualHost, cancellationToken), cancellationToken);

    [McpServerTool(Name = "rabbitmq_get_queue", Title = "Get RabbitMQ Queue", ReadOnly = true)]
    [Description("Get details for a RabbitMQ queue through the configured management API.")]
    public async Task<string> GetRabbitMqQueue(
        [Description("RabbitMQ virtual host.")] string virtualHost,
        [Description("Queue name.")] string queue,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("rabbitmq", "rabbitmq_get_queue", "get", $"{virtualHost}/{queue}", "shared", () => hub.GetRabbitMqQueueAsync(virtualHost, queue, cancellationToken), cancellationToken);

    [McpServerTool(Name = "rabbitmq_peek_queue", Title = "Peek RabbitMQ Queue", ReadOnly = true)]
    [Description("Non-destructively peek messages from a RabbitMQ queue. Messages are requeued; count and payload size are capped.")]
    public async Task<string> PeekRabbitMqQueue(
        [Description("RabbitMQ virtual host.")] string virtualHost,
        [Description("Queue name.")] string queue,
        [Description("Number of messages to peek. Defaults to 5, capped at 20.")] int? count = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("rabbitmq", "rabbitmq_peek_queue", "peek", $"{virtualHost}/{queue}", "shared", () => hub.PeekRabbitMqQueueAsync(virtualHost, queue, count, cancellationToken), cancellationToken);

    [McpServerTool(Name = "mysql_list_schemas", Title = "List MySQL Schemas", ReadOnly = true)]
    [Description("List indexed database schema projects through the MCP Hub MySQL provider.")]
    public async Task<string> ListMySqlSchemas(
        [Description("Optional search across schema project, server, and database names.")] string? search = null,
        [Description("Optional server name filter.")] string? server = null,
        [Description("Optional database name filter.")] string? database = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size.")] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("mysql", "mysql_list_schemas", "list", "indexed-schemas", "none", () => hub.ListSchemasAsync(search, server, database, page, pageSize, cancellationToken), cancellationToken);

    [McpServerTool(Name = "mysql_get_schema_catalog", Title = "Get MySQL Schema Catalog", ReadOnly = true)]
    [Description("Get a detailed catalog for an indexed database schema through the MCP Hub MySQL provider.")]
    public async Task<string> GetMySqlSchemaCatalog(
        [Description("Schema project name from mysql_list_schemas, for example db:server/database.")] string name,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("mysql", "mysql_get_schema_catalog", "get", name, "none", () => hub.GetSchemaCatalogAsync(name, cancellationToken), cancellationToken);

    [McpServerTool(Name = "mysql_readonly_query", Title = "Run MySQL Read-only Query", ReadOnly = true)]
    [Description("Run a bounded guarded read-only SQL statement against a configured MySQL source.")]
    public async Task<string> RunMySqlReadOnlyQuery(
        [Description("Credential source name. Use default when unsure.")] string source,
        [Description("A single SELECT, SHOW, DESCRIBE, DESC, or EXPLAIN statement.")] string sql,
        [Description("Maximum rows for SELECT results. Defaults to 100 and caps at 500.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("mysql", "mysql_readonly_query", "query", source, "shared", () => hub.RunReadOnlySqlAsync(source, sql, limit, cancellationToken), cancellationToken);

    private async Task<string> InvokeAuditedAsync(
        string providerKey,
        string toolName,
        string operation,
        string? resourceKey,
        string credentialMode,
        Func<Task<string>> action,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await action();
            await hub.AuditAsync(
                Username,
                TokenId,
                providerKey,
                toolName,
                "invoke",
                operation,
                resourceKey,
                credentialMode,
                "allowed",
                "ok",
                (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
                true,
                null,
                ct);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var statusClass = ex is McpHubProviderPolicyException ? "policy_denied" : "provider_error";
            await hub.AuditAsync(
                Username,
                TokenId,
                providerKey,
                toolName,
                "invoke",
                operation,
                resourceKey,
                credentialMode,
                statusClass == "policy_denied" ? "denied" : "allowed",
                statusClass,
                (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
                false,
                ex.Message,
                ct);
            return $"Provider call failed: {ex.Message}";
        }
    }

    private string? Username =>
        httpContextAccessor.HttpContext?.User.FindFirstValue("preferred_username") ??
        httpContextAccessor.HttpContext?.User.FindFirstValue("username") ??
        httpContextAccessor.HttpContext?.User.Identity?.Name;

    private long? TokenId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User.FindFirstValue("mcp_pat_token_id");
            return long.TryParse(value, out var parsed) ? parsed : null;
        }
    }
}
