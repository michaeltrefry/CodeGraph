using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Telemetry;

namespace CodeGraph.Api.Middleware;

public sealed class McpTelemetryMiddleware(
    RequestDelegate next,
    IServiceScopeFactory scopeFactory,
    ILogger<McpTelemetryMiddleware> logger)
{
    private static readonly PathString McpPath = new("/mcp");

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(McpPath) ||
            !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        var toolName = await TryReadToolNameAsync(context.Request, context.RequestAborted);
        if (toolName is null)
        {
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context);
            await PublishAsync(
                toolName,
                success: context.Response.StatusCode < StatusCodes.Status400BadRequest,
                durationMs: sw.ElapsedMilliseconds,
                context,
                errorCode: context.Response.StatusCode >= StatusCodes.Status400BadRequest
                    ? $"http_{context.Response.StatusCode}"
                    : null,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await PublishAsync(
                toolName,
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                context,
                errorCode: ex.GetType().Name,
                CancellationToken.None);
            throw;
        }
    }

    private async Task PublishAsync(
        string toolName,
        bool success,
        long durationMs,
        HttpContext context,
        string? errorCode,
        CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var metricsEventPublisher = scope.ServiceProvider.GetRequiredService<IMetricsEventPublisher>();
            await metricsEventPublisher.PublishMcpToolInvocationAsync(
                new McpToolInvocationRecord(
                    toolName,
                    success,
                    durationMs,
                    ResolveUsername(context),
                    ResolveTokenId(context),
                    errorCode),
                ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to record MCP telemetry for {ToolName}", toolName);
        }
    }

    private static async Task<string?> TryReadToolNameAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.Body is null || request.ContentLength == 0)
            return null;

        request.EnableBuffering();

        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            request.Body.Position = 0;

            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method) ||
                method.ValueKind != JsonValueKind.String ||
                !string.Equals(method.GetString(), "tools/call", StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("params", out var parameters) ||
                parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                return "unknown_tool";
            }

            return string.IsNullOrWhiteSpace(name.GetString())
                ? "unknown_tool"
                : name.GetString()!.Trim();
        }
        catch (JsonException)
        {
            if (request.Body.CanSeek)
                request.Body.Position = 0;
            return null;
        }
    }

    private static string? ResolveUsername(HttpContext context)
    {
        var username =
            context.User.FindFirstValue("preferred_username") ??
            context.User.FindFirstValue("username") ??
            context.User.Identity?.Name ??
            context.Request.Headers["X-CodeGraph-User"].FirstOrDefault();

        return string.IsNullOrWhiteSpace(username) ? null : username.Trim().ToLowerInvariant();
    }

    private static long? ResolveTokenId(HttpContext context)
    {
        var tokenId = context.User.FindFirstValue("mcp_pat_token_id");
        return long.TryParse(tokenId, out var parsed) ? parsed : null;
    }
}
