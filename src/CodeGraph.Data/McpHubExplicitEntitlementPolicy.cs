namespace CodeGraph.Data;

/// <summary>Capabilities that legacy all-mode PATs must never inherit implicitly.</summary>
public static class McpHubExplicitEntitlementPolicy
{
    private static readonly HashSet<string> ToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "shortcut-shared-api",
    };

    public static bool RequiresExplicitSelection(string toolName) => ToolNames.Contains(toolName);
}
