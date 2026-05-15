using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// Production <see cref="IMcpShimClient"/> built on the MCP SDK client over Streamable HTTP.
/// A fresh connection is opened per call — discovery and forwarding are low-frequency, admin- or
/// tool-invocation-triggered operations, so a persistent pooled session is not worth the
/// lifetime/cancellation complexity here.
/// </summary>
public sealed class McpClientShimClient(ILoggerFactory loggerFactory) : IMcpShimClient
{
    public async Task<IReadOnlyList<ShimToolDescriptor>> ListToolsAsync(
        Uri endpoint,
        string? bearerToken,
        CancellationToken ct = default)
    {
        await using var transport = CreateTransport(endpoint, bearerToken);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return tools
            .Select(tool => new ShimToolDescriptor(
                tool.Name,
                tool.Title,
                tool.Description,
                SerializeSchema(tool)))
            .ToList();
    }

    public async Task<ShimCallOutcome> CallToolAsync(
        Uri endpoint,
        string? bearerToken,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken ct = default)
    {
        await using var transport = CreateTransport(endpoint, bearerToken);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: ct);
        var node = JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions)
                   ?? new JsonObject();
        return new ShimCallOutcome(node, result.IsError == true);
    }

    private HttpClientTransport CreateTransport(Uri endpoint, string? bearerToken) =>
        new(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = "mcp-hub-shim",
            AdditionalHeaders = string.IsNullOrWhiteSpace(bearerToken)
                ? null
                : new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerToken.Trim()}" },
        }, loggerFactory);

    private static string? SerializeSchema(McpClientTool tool)
    {
        try
        {
            var schema = tool.JsonSchema;
            return schema.ValueKind == JsonValueKind.Undefined ? null : schema.GetRawText();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
