using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
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
    /// <summary>
    /// JSON-RPC error code (server-defined range) for a tools/call the hub refuses. The specific
    /// reason (tool_not_entitled, tool_disabled, ...) is in <c>error.data.error</c>.
    /// </summary>
    public const int ToolAccessDeniedErrorCode = -32003;

    /// <summary>JSON-RPC error code for a tools/list response the hub could not filter.</summary>
    public const int ToolsListUnfilterableErrorCode = -32603;

    private static readonly PathString McpPath = new("/mcp");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

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
            await InvokeAndFilterToolsListAsync(context, request);
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
            await RejectAsync(context, request.Id, null, new("tool_name_required", "MCP tools/call requests must include params.name."));
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpHubStore>();
        var catalog = await LoadCatalogAsync(store, context.RequestAborted);
        catalog.Tools.TryGetValue(toolName, out var tool);
        var denial = await EvaluateAccessAsync(store, catalog, toolName, tool, ResolveTokenId(context), context.RequestAborted);
        if (denial is not null)
        {
            await RejectAsync(context, request.Id, toolName, denial);
            return;
        }

        await DispatchAsync(context, tool!, request);
    }

    /// <summary>
    /// The single access predicate shared by tools/call and tools/list: a tool is listed exactly
    /// when a call to it would be dispatched. Returns null when access is granted, otherwise the
    /// machine-readable denial.
    /// </summary>
    private async Task<McpToolDenial?> EvaluateAccessAsync(
        IMcpHubStore store,
        McpCatalogSnapshot catalog,
        string toolName,
        McpHubToolEntity? tool,
        long? tokenId,
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

    private static async Task<McpCatalogSnapshot> LoadCatalogAsync(IMcpHubStore store, CancellationToken ct)
    {
        var providers = new Dictionary<string, McpHubProviderEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in await store.ListProvidersAsync(ct))
            providers.TryAdd(provider.ProviderKey, provider);

        var tools = new Dictionary<string, McpHubToolEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in await store.ListToolsAsync(ct))
            tools.TryAdd(tool.ToolName, tool);

        return new(providers, tools);
    }

    // Hands an entitled tools/call to its server. Native and first-party-provider tools are
    // [McpServerTool] methods served by the MCP SDK, so the request continues down the pipeline.
    // Shim tools have no SDK method — the hub forwards them to the downstream MCP server and
    // writes the JSON-RPC response itself.
    private async Task DispatchAsync(HttpContext context, McpHubToolEntity tool, McpRequest request)
    {
        if (!string.Equals(tool.ProviderType, "shim", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var shim = scope.ServiceProvider.GetRequiredService<McpShimService>();
        var resultNode = await shim.ForwardAsync(
            tool,
            ConvertArguments(request.Arguments),
            ResolveUsername(context),
            ResolveTokenId(context),
            context.RequestAborted);

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request.Id?.DeepClone(),
            ["result"] = resultNode,
        };
        await WriteJsonRpcAsync(context, response);
    }

    private static IReadOnlyDictionary<string, object?>? ConvertArguments(JsonNode? argumentsNode)
    {
        if (argumentsNode is not JsonObject argumentsObject)
            return null;

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in argumentsObject)
            arguments[property.Key] = property.Value?.DeepClone();
        return arguments;
    }

    private async Task InvokeAndFilterToolsListAsync(HttpContext context, McpRequest request)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
            buffer.Position = 0;
            var body = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync(context.RequestAborted);
            context.Response.Body = originalBody;

            if (context.Response.StatusCode >= StatusCodes.Status400BadRequest || string.IsNullOrWhiteSpace(body))
            {
                await WriteRawAsync(context, body);
                return;
            }

            var filtered = await TryFilterToolsListResponseAsync(context, body);
            if (filtered is null)
            {
                // Never pass an unfiltered tools/list through: it would advertise tools the
                // caller cannot call.
                logger.LogWarning(
                    "MCP tools/list response (content type {ContentType}) could not be filtered; refusing it",
                    context.Response.ContentType);
                await RejectAsync(
                    context,
                    request.Id,
                    null,
                    new("tools_list_unfilterable", "The MCP hub could not apply tool entitlements to the tool list."),
                    ToolsListUnfilterableErrorCode);
                return;
            }

            await WriteRawAsync(context, filtered);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static async Task WriteRawAsync(HttpContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    // The MCP SDK's streamable HTTP transport answers a POST with an SSE stream
    // ("event: message" / "data: {json-rpc}"), not a bare JSON body. The JSON-RPC message inside
    // each data line is filtered and the SSE framing is kept. A bare JSON body is handled too.
    // Returns null when a message could not be parsed as JSON-RPC (the caller then fails closed).
    // A parsed message without result.tools (e.g. a JSON-RPC error) passes through unchanged.
    private async Task<string?> TryFilterToolsListResponseAsync(HttpContext context, string body)
    {
        var isEventStream = context.Response.ContentType?
            .StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        if (!isEventStream)
            return await TryFilterToolsListMessageAsync(context, body);

        var lines = body.Split('\n');
        var sawMessage = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var carriageReturn = lines[i].EndsWith('\r');
            var line = carriageReturn ? lines[i][..^1] : lines[i];
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var filtered = await TryFilterToolsListMessageAsync(context, line["data:".Length..].TrimStart(' '));
            if (filtered is null)
                return null;

            lines[i] = "data: " + filtered + (carriageReturn ? "\r" : "");
            sawMessage = true;
        }

        return sawMessage ? string.Join('\n', lines) : null;
    }

    private async Task<string?> TryFilterToolsListMessageAsync(HttpContext context, string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null)
            return null;

        if (root["result"]?["tools"] is not JsonArray toolsArray)
            return json;

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpHubStore>();
        var (allowed, visibleShimTools) =
            await GetVisibleToolsAsync(store, ResolveTokenId(context), context.RequestAborted);

        // Drop SDK-advertised tools the caller cannot call.
        for (var i = toolsArray.Count - 1; i >= 0; i--)
        {
            var name = toolsArray[i]?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || !allowed.Contains(name))
                toolsArray.RemoveAt(i);
        }

        // Inject shim tools. They are discovered from a downstream MCP server and have no
        // [McpServerTool] method, so the SDK never lists them — the hub advertises them here
        // from the catalog so an entitled caller can discover and then call them.
        var present = toolsArray
            .Select(item => item?["name"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var shimTool in visibleShimTools)
        {
            if (present.Add(shimTool.ToolName))
                toolsArray.Add(BuildShimToolListEntry(shimTool));
        }

        return root.ToJsonString(WebJson);
    }

    private async Task<(HashSet<string> Names, List<McpHubToolEntity> ShimTools)> GetVisibleToolsAsync(
        IMcpHubStore store,
        long? tokenId,
        CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(store, ct);
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shimTools = new List<McpHubToolEntity>();
        foreach (var tool in catalog.Tools.Values)
        {
            if (await EvaluateAccessAsync(store, catalog, tool.ToolName, tool, tokenId, ct) is not null)
                continue;

            visible.Add(tool.ToolName);
            if (string.Equals(tool.ProviderType, "shim", StringComparison.OrdinalIgnoreCase))
                shimTools.Add(tool);
        }

        return (visible, shimTools);
    }

    private static JsonObject BuildShimToolListEntry(McpHubToolEntity tool)
    {
        JsonNode inputSchema;
        if (string.IsNullOrWhiteSpace(tool.InputSchema))
        {
            inputSchema = new JsonObject { ["type"] = "object" };
        }
        else
        {
            try
            {
                inputSchema = JsonNode.Parse(tool.InputSchema) ?? new JsonObject { ["type"] = "object" };
            }
            catch (JsonException)
            {
                inputSchema = new JsonObject { ["type"] = "object" };
            }
        }

        return new JsonObject
        {
            ["name"] = tool.ToolName,
            ["description"] = tool.Description,
            ["inputSchema"] = inputSchema,
        };
    }

    private static string? ResolveUsername(HttpContext context) =>
        context.User.FindFirstValue("preferred_username")
        ?? context.User.FindFirstValue("username")
        ?? context.User.Identity?.Name;

    // A refused request is answered as a JSON-RPC 2.0 error response echoing the request id, with
    // HTTP 200: the streamable HTTP transport carries JSON-RPC errors in a 200 application/json
    // response, and MCP clients only correlate and surface an error that arrives inside a
    // JSON-RPC envelope carrying the request id. The machine-readable reason is error.data.error.
    private static Task RejectAsync(
        HttpContext context,
        JsonNode? id,
        string? toolName,
        McpToolDenial denial,
        int code = ToolAccessDeniedErrorCode)
    {
        var data = new JsonObject { ["error"] = denial.Code };
        if (!string.IsNullOrWhiteSpace(toolName))
            data["tool"] = toolName;

        return WriteJsonRpcAsync(context, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = denial.Message,
                ["data"] = data,
            },
        });
    }

    private static async Task WriteJsonRpcAsync(HttpContext context, JsonObject response)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString(WebJson));
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
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
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("method", out var method) ||
                method.ValueKind != JsonValueKind.String)
            {
                return new(null, null);
            }

            // The JSON-RPC id is echoed back whenever the hub answers a request itself.
            var id = root.TryGetProperty("id", out var idElement)
                ? JsonNode.Parse(idElement.GetRawText())
                : null;

            var methodName = method.GetString();
            if (!string.Equals(methodName, "tools/call", StringComparison.Ordinal))
                return new(methodName, null, id);

            if (!root.TryGetProperty("params", out var parameters) ||
                parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                return new(methodName, null, id);
            }

            var arguments = parameters.TryGetProperty("arguments", out var argumentsElement) &&
                            argumentsElement.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(argumentsElement.GetRawText())
                : null;

            return new(
                methodName,
                string.IsNullOrWhiteSpace(name.GetString()) ? null : name.GetString()!.Trim(),
                id,
                arguments);
        }
        catch (JsonException)
        {
            if (request.Body.CanSeek)
                request.Body.Position = 0;
            return new(null, null);
        }
    }

    private sealed record McpToolDenial(string Code, string Message);

    private sealed record McpCatalogSnapshot(
        IReadOnlyDictionary<string, McpHubProviderEntity> Providers,
        IReadOnlyDictionary<string, McpHubToolEntity> Tools);

    private sealed record McpRequest(
        string? Method,
        string? ToolName,
        JsonNode? Id = null,
        JsonNode? Arguments = null);
}
