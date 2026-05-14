using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;

namespace CodeGraph.Api.Middleware;

public sealed class McpToolEntitlementMiddleware(
    RequestDelegate next,
    IServiceScopeFactory scopeFactory,
    IOptions<McpOptions> mcpOptions,
    IOptions<AuthOptions> authOptions,
    ILogger<McpToolEntitlementMiddleware> logger)
{
    private static readonly PathString McpPath = new("/mcp");

    // When either of these is on, the /mcp endpoint is protected by the PAT authorization
    // policy, so every request that reaches this middleware must carry a resolvable PAT
    // token id. A missing token id then means "fail closed", not "skip the check". When both
    // are off the MCP surface is intentionally open (dev mode) and there is nothing to check.
    private readonly bool requiresPersonalAccessToken =
        mcpOptions.Value.RequirePersonalAccessToken || authOptions.Value.Enabled;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(McpPath) ||
            !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        var request = await TryReadMcpRequestAsync(context.Request, context.RequestAborted);
        if (request.Method is null)
        {
            await next(context);
            return;
        }

        if (string.Equals(request.Method, "tools/list", StringComparison.Ordinal))
        {
            await InvokeAndFilterToolsListAsync(context, next);
            return;
        }

        if (!string.Equals(request.Method, "tools/call", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var toolName = request.ToolName;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            await RejectAsync(context, "tool_name_required", "MCP tools/call requests must include params.name.");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpHubStore>();
        var tools = await store.ListToolsAsync(context.RequestAborted);
        var tool = tools.FirstOrDefault(item => string.Equals(item.ToolName, toolName, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
        {
            await RejectAsync(context, "tool_not_cataloged", $"MCP tool '{toolName}' is not in the hub catalog.");
            return;
        }

        if (tool is { Enabled: false })
        {
            await RejectAsync(context, "tool_disabled", $"MCP tool '{toolName}' is disabled.");
            return;
        }

        if (tool is { IsAvailable: false })
        {
            await RejectAsync(context, "tool_unavailable", $"MCP tool '{toolName}' is not available in this deployment.");
            return;
        }

        var providers = await store.ListProvidersAsync(context.RequestAborted);
        var provider = providers.FirstOrDefault(item => string.Equals(item.ProviderKey, tool.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is { Enabled: false })
        {
            await RejectAsync(context, "provider_disabled", $"MCP provider '{tool.ProviderKey}' is disabled.");
            return;
        }

        var tokenId = ResolveTokenId(context);
        if (tokenId is null)
        {
            if (requiresPersonalAccessToken)
            {
                await RejectAsync(
                    context,
                    "token_required",
                    "This MCP request did not carry a personal access token, so tool entitlement could not be verified.");
                return;
            }

            await next(context);
            return;
        }

        try
        {
            if (await store.IsTokenEntitledAsync(tokenId.Value, toolName, context.RequestAborted))
            {
                await next(context);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to check MCP entitlement for token {TokenId} and tool {ToolName}", tokenId, toolName);
        }

        await RejectAsync(context, "tool_not_entitled", $"This MCP token is not entitled to call '{toolName}'.");
    }

    private async Task InvokeAndFilterToolsListAsync(HttpContext context, RequestDelegate next)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
            buffer.Position = 0;
            var body = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync(context.RequestAborted);
            var filtered = await TryFilterToolsListResponseAsync(context, body);
            var output = filtered ?? body;
            var bytes = Encoding.UTF8.GetBytes(output);
            context.Response.Body = originalBody;
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private async Task<string?> TryFilterToolsListResponseAsync(HttpContext context, string body)
    {
        if (context.Response.StatusCode >= StatusCodes.Status400BadRequest ||
            string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["result"]?["tools"] is not JsonArray toolsArray)
            return null;

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpHubStore>();
        var allowed = await GetVisibleToolNamesAsync(store, ResolveTokenId(context), context.RequestAborted);

        for (var i = toolsArray.Count - 1; i >= 0; i--)
        {
            var name = toolsArray[i]?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || !allowed.Contains(name))
                toolsArray.RemoveAt(i);
        }

        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private async Task<HashSet<string>> GetVisibleToolNamesAsync(
        IMcpHubStore store,
        long? tokenId,
        CancellationToken ct)
    {
        // Fail closed: when a PAT is required but the request carried no resolvable token id,
        // the caller sees an empty tool list rather than the full enabled catalog.
        if (tokenId is null && requiresPersonalAccessToken)
            return [];

        var providers = await store.ListProvidersAsync(ct);
        var enabledProviders = providers
            .Where(provider => provider.Enabled)
            .Select(provider => provider.ProviderKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in await store.ListToolsAsync(ct))
        {
            if (!tool.Enabled || !tool.IsAvailable || !enabledProviders.Contains(tool.ProviderKey))
                continue;

            if (tokenId is not null && !await store.IsTokenEntitledAsync(tokenId.Value, tool.ToolName, ct))
                continue;

            visible.Add(tool.ToolName);
        }

        return visible;
    }

    private static async Task RejectAsync(HttpContext context, string code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = code, message }, context.RequestAborted);
    }

    private static long? ResolveTokenId(HttpContext context)
    {
        var tokenId = context.User.FindFirstValue("mcp_pat_token_id");
        return long.TryParse(tokenId, out var parsed) ? parsed : null;
    }

    private static async Task<McpRequest> TryReadMcpRequestAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.Body is null || request.ContentLength == 0)
            return new(null, null);

        request.EnableBuffering();

        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            request.Body.Position = 0;

            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method) ||
                method.ValueKind != JsonValueKind.String)
            {
                return new(null, null);
            }

            var methodName = method.GetString();
            if (!string.Equals(methodName, "tools/call", StringComparison.Ordinal))
                return new(methodName, null);

            if (!root.TryGetProperty("params", out var parameters) ||
                parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                return new(methodName, null);
            }

            return new(methodName, string.IsNullOrWhiteSpace(name.GetString()) ? null : name.GetString()!.Trim());
        }
        catch (JsonException)
        {
            if (request.Body.CanSeek)
                request.Body.Position = 0;
            return new(null, null);
        }
    }

    private sealed record McpRequest(string? Method, string? ToolName);
}
