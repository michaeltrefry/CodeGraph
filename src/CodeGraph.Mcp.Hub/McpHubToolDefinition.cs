namespace CodeGraph.Mcp.Hub;

public sealed record McpHubProviderDefinition(
    string ProviderKey,
    string DisplayName,
    string Description,
    bool EnabledByDefault = false,
    bool SourceVisible = false);

public sealed record McpHubToolDefinition(
    string ToolName,
    string ProviderKey,
    string DisplayName,
    string Description,
    bool ReadOnly = true,
    bool Destructive = false,
    bool RequiresCredential = false,
    bool EnabledByDefault = false,
    bool DefaultSelected = false,
    string AccessClass = "read");
