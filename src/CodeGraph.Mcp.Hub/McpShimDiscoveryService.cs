using CodeGraph.Data;
using CodeGraph.Models.Responses;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// Thrown when a downstream-MCP shim discovery run cannot proceed — for example the provider
/// is not configured with a discovery URL, or the downstream server is unreachable.
/// </summary>
public sealed class McpShimDiscoveryException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Generic downstream-MCP shim discovery: connects to a configured downstream MCP server,
/// lists its tools, and syncs them into the hub catalog as <c>provider_type = shim</c> tools.
///
/// Discovery never silently enables a tool or grants it to existing tokens — newly discovered
/// tools are inserted disabled and not default-selected, and <see cref="IMcpHubStore.UpsertToolAsync"/>
/// preserves admin-owned <c>enabled</c> / <c>default_selected</c> / <c>access_class</c> on refresh.
/// Tools that have disappeared downstream are marked unavailable, not deleted, so admin state and
/// token entitlements survive a transient downstream change.
/// </summary>
public sealed class McpShimDiscoveryService(
    IMcpHubStore store,
    IMcpShimClient shimClient,
    ILogger<McpShimDiscoveryService> logger)
{
    // mcp_hub_config / mcp_hub_credentials keys, per provider, that point at the downstream server.
    public const string DiscoveryUrlConfigKey = "discoveryUrl";
    public const string DiscoveryTokenCredentialKey = "discoveryToken";

    /// <summary>Stable hub tool name for a downstream tool: <c>{providerKey}_{downstreamName}</c>.</summary>
    public static string ShimToolName(string providerKey, string downstreamName) =>
        $"{providerKey}_{downstreamName}";

    public async Task<McpShimDiscoveryResponse> DiscoverAsync(
        string providerKey,
        string? updatedBy,
        CancellationToken ct = default)
    {
        var provider = (await store.ListProvidersAsync(ct))
            .FirstOrDefault(item => string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            throw new McpShimDiscoveryException($"MCP provider '{providerKey}' is not registered.");

        var url = await store.GetConfigValueAsync(provider.ProviderKey, DiscoveryUrlConfigKey, ct);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var endpoint))
            throw new McpShimDiscoveryException(
                $"MCP provider '{providerKey}' has no valid '{DiscoveryUrlConfigKey}' configured. " +
                "Set it under MCP Hub config before running discovery.");

        var token = await store.GetCredentialValueAsync(provider.ProviderKey, DiscoveryTokenCredentialKey, ct);

        IReadOnlyList<ShimToolDescriptor> downstreamTools;
        try
        {
            downstreamTools = await shimClient.ListToolsAsync(endpoint, token, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or McpShimDiscoveryException))
        {
            logger.LogWarning(ex, "MCP shim discovery failed for provider {ProviderKey}", provider.ProviderKey);
            throw new McpShimDiscoveryException(
                $"Could not discover tools from the downstream MCP server for '{providerKey}': {ex.Message}", ex);
        }

        var now = DateTime.UtcNow;
        var discovered = new List<string>();
        foreach (var downstream in downstreamTools)
        {
            var toolName = ShimToolName(provider.ProviderKey, downstream.Name);
            await store.UpsertToolAsync(new McpHubToolEntity
            {
                ToolName = toolName,
                ProviderKey = provider.ProviderKey,
                ProviderType = "shim",
                DisplayName = string.IsNullOrWhiteSpace(downstream.Title) ? downstream.Name : downstream.Title!,
                Description = downstream.Description ?? string.Empty,
                // Downstream tools/list does not reliably carry read-only/destructive hints, so a
                // discovered tool is treated conservatively and stays admin-gated.
                ReadOnly = false,
                Destructive = false,
                // First discovery: inactive and not default-selected so it is never silently
                // granted. UpsertToolAsync preserves these admin-owned columns on later refreshes.
                Enabled = false,
                IsAvailable = true,
                DefaultSelected = false,
                AccessClass = "read",
                RequiresCredential = false,
                InputSchema = downstream.InputSchemaJson,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }, ct);
            discovered.Add(toolName);
        }

        // Tools that vanished downstream are retired (is_available = false), not deleted.
        var discoveredSet = discovered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retired = new List<string>();
        foreach (var existing in await store.ListToolsAsync(ct))
        {
            if (existing.ProviderType != "shim" ||
                !string.Equals(existing.ProviderKey, provider.ProviderKey, StringComparison.OrdinalIgnoreCase) ||
                !existing.IsAvailable ||
                discoveredSet.Contains(existing.ToolName))
            {
                continue;
            }

            existing.IsAvailable = false;
            existing.UpdatedAtUtc = now;
            await store.UpsertToolAsync(existing, ct);
            retired.Add(existing.ToolName);
        }

        logger.LogInformation(
            "MCP shim discovery for {ProviderKey} by {UpdatedBy}: {Discovered} discovered, {Retired} retired",
            provider.ProviderKey, updatedBy ?? "(unknown)", discovered.Count, retired.Count);

        return new McpShimDiscoveryResponse(provider.ProviderKey, discovered.Count, retired.Count, discovered);
    }
}
