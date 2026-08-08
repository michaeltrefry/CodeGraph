using System.Net;
using System.Text.Json;
using CodeGraph.Api.Middleware;
using CodeGraph.Indexer.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Middleware;

public sealed class IndexerClientExceptionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ReturnsStructuredIndexerFailureWithoutAStackTrace()
    {
        var middleware = new IndexerClientExceptionMiddleware(
            _ => throw new IndexerClientException(
                HttpStatusCode.InternalServerError,
                "rust_semantic_command_timeout",
                "Rust semantic indexing timed out."),
            NullLogger<IndexerClientExceptionMiddleware>.Instance);
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-42",
            Response = { Body = new MemoryStream() }
        };

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        context.Response.ContentType.ShouldStartWith("application/json");
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        document.RootElement.GetProperty("error").GetString().ShouldBe("rust_semantic_command_timeout");
        document.RootElement.GetProperty("message").GetString().ShouldBe("Rust semantic indexing timed out.");
        document.RootElement.GetProperty("traceId").GetString().ShouldBe("trace-42");
        document.RootElement.ToString().ShouldNotContain("StackTrace");
    }
}
