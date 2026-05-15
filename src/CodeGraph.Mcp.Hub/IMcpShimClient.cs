using System.Text.Json.Nodes;

namespace CodeGraph.Mcp.Hub;

/// <summary>A tool advertised by a downstream MCP server's <c>tools/list</c>.</summary>
public sealed record ShimToolDescriptor(
    string Name,
    string? Title,
    string? Description,
    string? InputSchemaJson);

/// <summary>
/// The outcome of forwarding a <c>tools/call</c> to a downstream MCP server. <see cref="Result"/>
/// is the downstream <c>CallToolResult</c> serialized as JSON — the exact shape that belongs in a
/// JSON-RPC <c>result</c> field.
/// </summary>
public sealed record ShimCallOutcome(JsonNode Result, bool IsError);

/// <summary>
/// Transport seam for talking to a downstream MCP server. The production implementation wraps the
/// MCP SDK client; tests substitute a fake so discovery/forwarding logic is exercised without a
/// live downstream server.
/// </summary>
public interface IMcpShimClient
{
    /// <summary>Connects to the downstream MCP server and returns its advertised tools.</summary>
    Task<IReadOnlyList<ShimToolDescriptor>> ListToolsAsync(
        Uri endpoint,
        string? bearerToken,
        CancellationToken ct = default);

    /// <summary>Forwards a <c>tools/call</c> to the downstream MCP server.</summary>
    Task<ShimCallOutcome> CallToolAsync(
        Uri endpoint,
        string? bearerToken,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken ct = default);
}
