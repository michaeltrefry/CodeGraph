namespace CodeGraph.Data;

/// <summary>
/// High-risk tools that legacy "all" tokens must not inherit. They require a token switched to
/// selected-tool mode so the capability is granted deliberately.
/// </summary>
public static class McpHubExplicitEntitlementPolicy
{
    private static readonly HashSet<string> ToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "shortcut-shared-api",
        "stories-stage-file",
        "stories-upload-file",
    };

    /// <summary>
    /// The existing memory tools (D17, R33): hidden from the default listing, their data kept. An
    /// all-mode token neither lists nor calls them; a token whose owner selected them keeps them. The
    /// project knowledge store (record / recall) replaces them for agents.
    /// </summary>
    public static readonly IReadOnlySet<string> HiddenMemoryTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "store_memory_v2", "get_memory_write_status", "query_memory", "search_memory", "get_memory_subgraph",
        "get_entity_bundle", "get_claim_bundle", "expand_memory_frontier", "render_memory_summary",
        "memory_read", "memory_store", "memory_diagnostics",
    };

    public static bool RequiresExplicitSelection(string toolName) => ToolNames.Contains(toolName) || HiddenMemoryTools.Contains(toolName);
}
