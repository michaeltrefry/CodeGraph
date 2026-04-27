using System.Security.Claims;
using System.Text;
using CodeGraph.Api.Middleware;
using CodeGraph.Data;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Api;

public class McpTelemetryMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_RecordsToolInvocation_AndLeavesBodyReadable()
    {
        var publisher = new RecordingMetricsEventPublisher();
        string? downstreamBody = null;
        var middleware = new McpTelemetryMiddleware(
            async context =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                downstreamBody = await reader.ReadToEndAsync();
                context.Response.StatusCode = StatusCodes.Status200OK;
            },
            publisher,
            NullLogger<McpTelemetryMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_graph","arguments":{"query":"CodeGraph"}}}
            """));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("preferred_username", "Michael"),
                new Claim("mcp_pat_token_id", "42")
            ],
            "McpPat"));

        await middleware.InvokeAsync(context);

        downstreamBody.ShouldNotBeNull();
        downstreamBody.ShouldContain("\"tools/call\"");
        var invocation = publisher.ToolInvocations.Single();
        invocation.ToolName.ShouldBe("search_graph");
        invocation.Success.ShouldBeTrue();
        invocation.Username.ShouldBe("michael");
        invocation.TokenId.ShouldBe(42);
    }

    private sealed class RecordingMetricsEventPublisher : IMetricsEventPublisher
    {
        public List<McpToolInvocationRecord> ToolInvocations { get; } = [];

        public Task<LlmUsageRecord> PublishLlmUsageAsync(LlmUsageRecord usage, CancellationToken ct = default) =>
            Task.FromResult(usage);

        public Task<IReadOnlyList<LlmUsageRecord>> PublishLlmUsageBatchAsync(
            IEnumerable<LlmUsageRecord> usage,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LlmUsageRecord>>(usage.ToList());

        public Task<McpToolInvocationRecord> PublishMcpToolInvocationAsync(
            McpToolInvocationRecord invocation,
            CancellationToken ct = default)
        {
            ToolInvocations.Add(invocation);
            return Task.FromResult(invocation);
        }
    }
}
