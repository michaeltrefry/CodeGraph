namespace CodeGraph.Data;

/// <summary>
/// High-risk tools that legacy "all" tokens must not inherit. They require a token switched to
/// selected-tool mode so the capability is granted deliberately.
/// </summary>
public static class McpHubExplicitEntitlementPolicy
{
    private static readonly HashSet<string> ToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "stories-stage-file",
        "stories-upload-file",
    };

    public static bool RequiresExplicitSelection(string toolName) => ToolNames.Contains(toolName);
}
