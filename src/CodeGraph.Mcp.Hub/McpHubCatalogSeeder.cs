using System.Reflection;
using CodeGraph.Data;
using CodeGraph.Services.Assistant;
using ModelContextProtocol.Server;

namespace CodeGraph.Mcp.Hub;

public sealed class McpHubCatalogSeeder(IMcpHubStore store, IMcpSensitiveColumnStore sensitiveColumnStore)
{
    // Common sensitive column-name patterns seeded as global (source='*', table='*') deny rows.
    // Admins can add source/table-specific rows or "allowed" overrides on top — see Shortcut sc-1051.
    private static readonly string[] SensitiveColumnPatterns =
    [
        "password", "passwd", "pwd", "password_hash", "passwordhash",
        "secret", "client_secret", "salt",
        "token", "access_token", "refresh_token", "session_token", "api_token",
        "api_key", "apikey", "access_key", "secret_key", "private_key",
        "ssn", "social_security_number", "tax_id",
        "credit_card", "card_number", "cvv", "cvc",
    ];

    private static readonly McpHubProviderDefinition[] Providers =
    [
        new("codegraph", "CodeGraph", "Native CodeGraph graph, memory, convention, repository, and schema tools.", EnabledByDefault: true, SourceVisible: true),
        new("shortcut", "Shortcut", "Delegated Shortcut project tracking tools using provider credentials."),
        new("rabbitmq", "RabbitMQ", "Read-only RabbitMQ management API inspection tools."),
        new("mysql", "MySQL", "Schema catalog and guarded read-only SQL tools.")
    ];

    private static readonly McpHubToolDefinition[] ProviderTools =
    [
        new("mcp_hub_catalog", "codegraph", "MCP Hub catalog", "List MCP providers, tools, entitlement flags, and enabled state.", EnabledByDefault: true, DefaultSelected: true),
        new("shortcut_search_epics", "shortcut", "Search Shortcut epics", "Search Shortcut epics through the delegated Shortcut provider.", RequiresCredential: true),
        new("shortcut_search_stories", "shortcut", "Search Shortcut stories", "Search Shortcut stories through the delegated Shortcut provider.", RequiresCredential: true),
        new("rabbitmq_list_queues", "rabbitmq", "List RabbitMQ queues", "List queues through the RabbitMQ management API.", RequiresCredential: true),
        new("rabbitmq_get_queue", "rabbitmq", "Get RabbitMQ queue", "Get read-only details for one RabbitMQ queue.", RequiresCredential: true),
        new("rabbitmq_peek_queue", "rabbitmq", "Peek RabbitMQ queue", "Non-destructively peek capped messages from a RabbitMQ queue.", RequiresCredential: true),
        new("mysql_list_schemas", "mysql", "List indexed MySQL schemas", "List indexed schema projects and counts."),
        new("mysql_get_schema_catalog", "mysql", "Get indexed MySQL schema catalog", "Return indexed tables, views, procedures, columns, indexes, and constraints."),
        new("mysql_readonly_query", "mysql", "Run guarded read-only SQL", "Run a bounded SELECT/SHOW/DESCRIBE/EXPLAIN query against a configured source.", RequiresCredential: true)
    ];

    public async Task EnsureCatalogAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        foreach (var provider in Providers)
        {
            await store.UpsertProviderAsync(new McpHubProviderEntity
            {
                ProviderKey = provider.ProviderKey,
                DisplayName = provider.DisplayName,
                Description = provider.Description,
                Enabled = provider.EnabledByDefault,
                SourceVisible = provider.SourceVisible,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }, ct);
        }

        foreach (var tool in ProviderTools.Concat(GetNativeToolDefinitions()))
        {
            await store.UpsertToolAsync(new McpHubToolEntity
            {
                ToolName = tool.ToolName,
                ProviderKey = tool.ProviderKey,
                DisplayName = tool.DisplayName,
                Description = tool.Description,
                ReadOnly = tool.ReadOnly,
                Destructive = tool.Destructive,
                Enabled = tool.EnabledByDefault,
                // is_available is system-owned: every tool the seeder registers exists in this
                // deployment by definition. Enabled / DefaultSelected / AccessClass are admin-owned
                // and only seeded on first insert (UpsertToolAsync preserves them afterwards).
                IsAvailable = true,
                DefaultSelected = tool.DefaultSelected,
                AccessClass = tool.AccessClass,
                RequiresCredential = tool.RequiresCredential,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }, ct);
        }

        await SeedSensitiveColumnPatternsAsync(ct);
    }

    // Idempotent: only adds a global pattern row when one does not already exist, so admin
    // "allowed" overrides and manual entries on the same (source='*', table='*', column) are
    // never clobbered by re-seeding.
    private async Task SeedSensitiveColumnPatternsAsync(CancellationToken ct)
    {
        var existing = await sensitiveColumnStore.ListAsync(ct);
        var existingGlobal = existing
            .Where(row => row.SourceKey == "*" && row.TableName == "*")
            .Select(row => row.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in SensitiveColumnPatterns)
        {
            if (existingGlobal.Contains(pattern))
                continue;

            await sensitiveColumnStore.UpsertAsync(new McpSensitiveColumnEntity
            {
                SourceKey = "*",
                TableName = "*",
                ColumnName = pattern,
                Reason = "Seeded sensitive-column pattern",
                Allowed = false,
                IsManual = false,
            }, ct);
        }
    }

    // Legacy narrow tools and the consolidated intent-family tools are both cataloged
    // (additive advertisement). Legacy tools can be hidden via hub catalog policy after
    // parity with the consolidated tools is validated.
    private static IEnumerable<McpHubToolDefinition> GetNativeToolDefinitions() =>
        GetNativeToolDefinitions(typeof(CodeGraphMcpServer))
            .Concat(GetNativeToolDefinitions(typeof(MemoryMcpServer)))
            .Concat(GetNativeToolDefinitions(typeof(ConsolidatedMcpServer)));

    private static IEnumerable<McpHubToolDefinition> GetNativeToolDefinitions(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute is null)
                continue;

            var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description
                ?? attribute.Title
                ?? attribute.Name
                ?? method.Name;
            var name = string.IsNullOrWhiteSpace(attribute.Name) ? method.Name : attribute.Name;
            var readOnly = GetBoolProperty(attribute, nameof(McpServerToolAttribute.ReadOnly), defaultValue: true);
            var destructive = GetBoolProperty(attribute, nameof(McpServerToolAttribute.Destructive), defaultValue: false);
            yield return new McpHubToolDefinition(
                name!,
                "codegraph",
                attribute.Title ?? name!,
                description,
                readOnly,
                destructive,
                EnabledByDefault: true,
                // Native CodeGraph tools are sensible defaults to pre-check at token creation.
                DefaultSelected: true,
                // UI grouping label derived from the tool's declared MCP hints. Note that many
                // CodeGraph tool attributes do not set ReadOnly = true, so they fall into "write"
                // here — tightening those attribute hints is separate attribute-hygiene work.
                AccessClass: readOnly ? "read" : "write");
        }
    }

    private static bool GetBoolProperty(object source, string propertyName, bool defaultValue)
    {
        var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        return value is bool boolean ? boolean : defaultValue;
    }
}
