using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// Forwards an entitled <c>tools/call</c> for a <c>provider_type = shim</c> hub tool to its
/// downstream MCP server, and writes a hub audit record for the invocation. Entitlement is
/// enforced upstream (by the MCP tool entitlement middleware) before forwarding ever happens.
/// </summary>
public sealed class McpShimService(
    IMcpHubStore store,
    IMcpShimClient shimClient,
    ILogger<McpShimService> logger)
{
    /// <summary>Recovers the downstream tool name from a <c>{providerKey}_{downstreamName}</c> hub tool name.</summary>
    public static string DownstreamToolName(McpHubToolEntity shimTool)
    {
        var prefix = shimTool.ProviderKey + "_";
        return shimTool.ToolName.StartsWith(prefix, StringComparison.Ordinal)
            ? shimTool.ToolName[prefix.Length..]
            : shimTool.ToolName;
    }

    /// <summary>
    /// Forwards the call and returns the JSON node that belongs in the JSON-RPC <c>result</c>
    /// field. Downstream or transport failures are surfaced as a tool result with
    /// <c>isError = true</c> rather than thrown, so the caller always gets a usable response.
    /// </summary>
    public async Task<JsonNode> ForwardAsync(
        McpHubToolEntity shimTool,
        IReadOnlyDictionary<string, object?>? arguments,
        string? username,
        long? tokenId,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var downstreamName = DownstreamToolName(shimTool);

        var url = await store.GetConfigValueAsync(
            shimTool.ProviderKey, McpShimDiscoveryService.DiscoveryUrlConfigKey, ct);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var endpoint))
        {
            await AuditAsync(shimTool, downstreamName, username, tokenId, sw,
                success: false, statusClass: "provider_error",
                message: $"No valid '{McpShimDiscoveryService.DiscoveryUrlConfigKey}' configured.", ct);
            return ErrorResult($"MCP shim provider '{shimTool.ProviderKey}' is not configured.");
        }

        var token = await store.GetCredentialValueAsync(
            shimTool.ProviderKey, McpShimDiscoveryService.DiscoveryTokenCredentialKey, ct);

        try
        {
            var outcome = await shimClient.CallToolAsync(endpoint, token, downstreamName, arguments, ct);
            await AuditAsync(shimTool, downstreamName, username, tokenId, sw,
                success: !outcome.IsError,
                statusClass: outcome.IsError ? "provider_error" : "ok",
                message: outcome.IsError ? "Downstream tool reported an error." : null, ct);
            return outcome.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MCP shim forward failed for {ToolName}", shimTool.ToolName);
            await AuditAsync(shimTool, downstreamName, username, tokenId, sw,
                success: false, statusClass: "provider_error", message: ex.Message, ct);
            return ErrorResult($"Downstream MCP call failed: {ex.Message}");
        }
    }

    private async Task AuditAsync(
        McpHubToolEntity shimTool,
        string downstreamName,
        string? username,
        long? tokenId,
        Stopwatch sw,
        bool success,
        string statusClass,
        string? message,
        CancellationToken ct)
    {
        try
        {
            await store.CreateAuditAsync(new McpHubAuditEntity
            {
                Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
                TokenId = tokenId,
                ProviderKey = shimTool.ProviderKey,
                ProviderType = "shim",
                ToolName = shimTool.ToolName,
                Action = "invoke",
                // The downstream tool name is the operation / resource identity for a shim call.
                Operation = downstreamName,
                ResourceKey = downstreamName,
                // The shim forwards using the hub-shared discovery credential, not a per-user one.
                CredentialMode = "shared",
                // Entitlement is enforced by the middleware before ForwardAsync is ever called.
                AuthorizationDecision = "allowed",
                StatusClass = statusClass,
                DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
                Success = success,
                Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
            }, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to write MCP shim audit for {ToolName}", shimTool.ToolName);
        }
    }

    private static JsonNode ErrorResult(string message) =>
        new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
            ["isError"] = true,
        };
}
