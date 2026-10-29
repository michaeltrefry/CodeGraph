using CodeGraph.Data;

namespace CodeGraph.Api.Middleware;

/// <summary>A refused tool call: the machine-readable reason (<c>tool_not_entitled</c>, ...) and its message.</summary>
public sealed record McpToolDenial(string Code, string Message);

/// <summary>The hub catalog as one request sees it.</summary>
public sealed record McpCatalogSnapshot(
    IReadOnlyDictionary<string, McpHubProviderEntity> Providers,
    IReadOnlyDictionary<string, McpHubToolEntity> Tools);

/// <summary>
/// The single access predicate of the hub: a tool is listed exactly when a call to it would be
/// dispatched. Shared by <see cref="McpToolEntitlementMiddleware"/> (MCP tools/list and tools/call)
/// and by REST routes that run the same tool (the work tools), so a token's selection, a disabled
/// tool and a disabled provider apply to both surfaces alike.
/// </summary>
public static class McpToolAccess
{
    public static async Task<McpCatalogSnapshot> LoadCatalogAsync(IMcpHubStore store, CancellationToken ct)
    {
        var providers = new Dictionary<string, McpHubProviderEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in await store.ListProvidersAsync(ct))
            providers.TryAdd(provider.ProviderKey, provider);

        var tools = new Dictionary<string, McpHubToolEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in await store.ListToolsAsync(ct))
            tools.TryAdd(tool.ToolName, tool);

        return new(providers, tools);
    }

    /// <summary>Loads the catalog and evaluates one tool: null when access is granted.</summary>
    public static async Task<McpToolDenial?> EvaluateAsync(
        IMcpHubStore store,
        string toolName,
        long? tokenId,
        bool requiresPersonalAccessToken,
        ILogger logger,
        CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(store, ct);
        catalog.Tools.TryGetValue(toolName, out var tool);
        return await EvaluateAsync(store, catalog, toolName, tool, tokenId, requiresPersonalAccessToken, logger, ct);
    }

    /// <summary>Returns null when access is granted, otherwise the machine-readable denial.</summary>
    public static async Task<McpToolDenial?> EvaluateAsync(
        IMcpHubStore store,
        McpCatalogSnapshot catalog,
        string toolName,
        McpHubToolEntity? tool,
        long? tokenId,
        bool requiresPersonalAccessToken,
        ILogger logger,
        CancellationToken ct)
    {
        if (tool is null)
            return new("tool_not_cataloged", $"MCP tool '{toolName}' is not in the hub catalog.");

        if (!tool.Enabled)
            return new("tool_disabled", $"MCP tool '{toolName}' is disabled.");

        if (!tool.IsAvailable)
            return new("tool_unavailable", $"MCP tool '{toolName}' is not available in this deployment.");

        if (catalog.Providers.TryGetValue(tool.ProviderKey, out var provider) && !provider.Enabled)
            return new("provider_disabled", $"MCP provider '{tool.ProviderKey}' is disabled.");

        if (tokenId is null)
        {
            return requiresPersonalAccessToken
                ? new("token_required", "This MCP request did not carry a personal access token, so tool entitlement could not be verified.")
                : null;
        }

        try
        {
            if (await store.IsTokenEntitledAsync(tokenId.Value, tool.ToolName, ct))
                return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to check MCP entitlement for token {TokenId} and tool {ToolName}", tokenId, toolName);
        }

        return new("tool_not_entitled", $"This MCP token is not entitled to call '{toolName}'.");
    }
}
