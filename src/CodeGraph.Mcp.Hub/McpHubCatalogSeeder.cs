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
        new("mysql", "MySQL", "Schema catalog and guarded read-only SQL tools."),
        // Shim provider: tools are not seeded here — an admin sets discoveryUrl/discoveryToken
        // and runs discovery, which populates provider_type='shim' tools (disabled by default).
        new("shortcut-shim", "Shortcut (MCP shim)", "Retired downstream Shortcut MCP shim. Use the native Shortcut provider instead.")
    ];

    private static readonly McpHubToolDefinition[] ProviderTools =
    [
        new("mcp_hub_catalog", "codegraph", "MCP Hub catalog", "List MCP providers, tools, entitlement flags, and enabled state.", EnabledByDefault: true, DefaultSelected: true, ProviderType: "native"),
        ShortcutRead("shortcut_search_epics", "Search Shortcut epics (legacy)", "Legacy alias for epics-search."),
        ShortcutRead("shortcut_search_stories", "Search Shortcut stories (legacy)", "Legacy alias for stories-search."),
        ShortcutRead("stories-get-by-id", "Get Shortcut story", "Get a single Shortcut story by ID."),
        ShortcutRead("stories-get-history", "Get Shortcut story history", "Get the change history for a story."),
        ShortcutRead("stories-search", "Search Shortcut stories", "Find Shortcut stories with Shortcut search syntax."),
        ShortcutRead("stories-get-branch-name", "Get Shortcut story branch name", "Get the recommended branch name for a story."),
        ShortcutWrite("stories-create", "Create Shortcut story", "Create a new Shortcut story."),
        ShortcutWrite("stories-update", "Update Shortcut story", "Update an existing Shortcut story."),
        ShortcutWrite("stories-upload-file", "Upload Shortcut story file", "Upload a file and link it to a story."),
        ShortcutWrite("stories-assign-current-user", "Assign current Shortcut user", "Assign the current user as a story owner."),
        ShortcutWrite("stories-unassign-current-user", "Unassign current Shortcut user", "Remove the current user as a story owner."),
        ShortcutWrite("stories-create-comment", "Create Shortcut story comment", "Create a comment on a story."),
        ShortcutWrite("stories-create-subtask", "Create Shortcut sub-task", "Create a new sub-task story."),
        ShortcutWrite("stories-add-subtask", "Add Shortcut sub-task", "Add an existing story as a sub-task."),
        ShortcutWrite("stories-remove-subtask", "Remove Shortcut sub-task", "Remove a sub-task relationship.", Destructive: true),
        ShortcutWrite("stories-add-task", "Add Shortcut story task", "Add a checklist task to a story."),
        ShortcutWrite("stories-update-task", "Update Shortcut story task", "Update a checklist task on a story."),
        ShortcutWrite("stories-add-relation", "Add Shortcut story relation", "Add a relationship between stories."),
        ShortcutWrite("stories-add-external-link", "Add Shortcut story external link", "Add an external link to a story."),
        ShortcutWrite("stories-remove-external-link", "Remove Shortcut story external link", "Remove an external link from a story.", Destructive: true),
        ShortcutWrite("stories-set-external-links", "Set Shortcut story external links", "Replace all external links on a story.", Destructive: true),
        ShortcutRead("stories-get-by-external-link", "Get Shortcut stories by external link", "Find stories that contain a specific external link."),
        ShortcutRead("labels-list", "List Shortcut labels", "List all labels in the Shortcut workspace."),
        ShortcutRead("labels-get-stories", "Get Shortcut label stories", "Get all stories with a specific label."),
        ShortcutWrite("labels-create", "Create Shortcut label", "Create a new Shortcut label."),
        ShortcutRead("custom-fields-list", "List Shortcut custom fields", "List custom fields and their possible values."),
        ShortcutRead("epics-get-by-id", "Get Shortcut epic", "Get a single Shortcut epic by ID."),
        ShortcutRead("epics-search", "Search Shortcut epics", "Find Shortcut epics with Shortcut search syntax."),
        ShortcutWrite("epics-create", "Create Shortcut epic", "Create a new Shortcut epic."),
        ShortcutWrite("epics-update", "Update Shortcut epic", "Update an existing Shortcut epic."),
        ShortcutWrite("epics-delete", "Delete Shortcut epic", "Delete a Shortcut epic.", Destructive: true),
        ShortcutWrite("epics-create-comment", "Create Shortcut epic comment", "Create a comment on an epic."),
        ShortcutRead("iterations-get-stories", "Get Shortcut iteration stories", "Get stories in a Shortcut iteration."),
        ShortcutRead("iterations-get-by-id", "Get Shortcut iteration", "Get a Shortcut iteration by ID."),
        ShortcutRead("iterations-search", "Search Shortcut iterations", "Find Shortcut iterations."),
        ShortcutWrite("iterations-create", "Create Shortcut iteration", "Create a new Shortcut iteration."),
        ShortcutWrite("iterations-update", "Update Shortcut iteration", "Update an existing Shortcut iteration."),
        ShortcutWrite("iterations-delete", "Delete Shortcut iteration", "Delete a Shortcut iteration.", Destructive: true),
        ShortcutRead("iterations-get-active", "Get active Shortcut iterations", "Get active iterations for the current user or team."),
        ShortcutRead("iterations-get-upcoming", "Get upcoming Shortcut iterations", "Get upcoming iterations for the current user or team."),
        ShortcutRead("objectives-get-by-id", "Get Shortcut objective", "Get a Shortcut objective by ID."),
        ShortcutRead("objectives-search", "Search Shortcut objectives", "Find Shortcut objectives."),
        ShortcutRead("teams-get-by-id", "Get Shortcut team", "Get a Shortcut team by ID."),
        ShortcutRead("teams-list", "List Shortcut teams", "List all Shortcut teams."),
        ShortcutRead("projects-list", "List Shortcut projects", "List all Shortcut projects."),
        ShortcutRead("projects-get-by-id", "Get Shortcut project", "Get a Shortcut project by ID."),
        ShortcutRead("projects-get-stories", "Get Shortcut project stories", "Get stories in a Shortcut project."),
        ShortcutRead("workflows-get-default", "Get default Shortcut workflow", "Get the default workflow for a team or workspace."),
        ShortcutRead("workflows-get-by-id", "Get Shortcut workflow", "Get a Shortcut workflow by ID."),
        ShortcutRead("workflows-list", "List Shortcut workflows", "List all Shortcut workflows."),
        ShortcutRead("users-get-current", "Get current Shortcut user", "Get current user information."),
        ShortcutRead("users-get-current-teams", "Get current Shortcut user teams", "Get teams where the current user is a member."),
        ShortcutRead("users-list", "List Shortcut users", "Get all workspace users."),
        ShortcutWrite("documents-create", "Create Shortcut document", "Create a Shortcut document with Markdown content."),
        ShortcutWrite("documents-update", "Update Shortcut document", "Update a Shortcut document."),
        ShortcutRead("documents-list", "List Shortcut documents", "List Shortcut documents."),
        ShortcutRead("documents-search", "Search Shortcut documents", "Search Shortcut documents."),
        ShortcutRead("documents-get-by-id", "Get Shortcut document", "Get a Shortcut document by ID."),
        new("rabbitmq_list_queues", "rabbitmq", "List RabbitMQ queues", "List queues through the RabbitMQ management API.", RequiresCredential: true),
        new("rabbitmq_get_queue", "rabbitmq", "Get RabbitMQ queue", "Get read-only details for one RabbitMQ queue.", RequiresCredential: true),
        new("rabbitmq_peek_queue", "rabbitmq", "Peek RabbitMQ queue", "Non-destructively peek capped messages from a RabbitMQ queue.", RequiresCredential: true),
        new("mysql_list_schemas", "mysql", "List indexed MySQL schemas", "List indexed schema projects and counts."),
        new("mysql_get_schema_catalog", "mysql", "Get indexed MySQL schema catalog", "Return indexed tables, views, procedures, columns, indexes, and constraints."),
        new("mysql_readonly_query", "mysql", "Run guarded read-only SQL", "Run a bounded SELECT/SHOW/DESCRIBE/EXPLAIN query against a configured source.", RequiresCredential: true)
    ];

    private static McpHubToolDefinition ShortcutRead(string toolName, string displayName, string description) =>
        new(toolName, "shortcut", displayName, description, ReadOnly: true, RequiresCredential: true);

    private static McpHubToolDefinition ShortcutWrite(
        string toolName,
        string displayName,
        string description,
        bool Destructive = false) =>
        new(toolName, "shortcut", displayName, description, ReadOnly: false, Destructive: Destructive, RequiresCredential: true, AccessClass: "write");

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
                ProviderType = tool.ProviderType,
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
                AccessClass: readOnly ? "read" : "write",
                ProviderType: "native");
        }
    }

    private static bool GetBoolProperty(object source, string propertyName, bool defaultValue)
    {
        var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        return value is bool boolean ? boolean : defaultValue;
    }
}
