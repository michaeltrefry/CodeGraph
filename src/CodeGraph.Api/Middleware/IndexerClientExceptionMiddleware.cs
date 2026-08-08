using System.Diagnostics;
using CodeGraph.Indexer.Client;

namespace CodeGraph.Api.Middleware;

public sealed class IndexerClientExceptionMiddleware(
    RequestDelegate next,
    ILogger<IndexerClientExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (IndexerClientException ex) when (!context.Response.HasStarted)
        {
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            logger.LogWarning(
                ex,
                "Indexer request failed with {StatusCode} and error code {ErrorCode}; trace {TraceId}",
                (int)ex.StatusCode,
                ex.ErrorCode,
                traceId);

            context.Response.Clear();
            context.Response.StatusCode = (int)ex.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = ex.ErrorCode ?? "indexer_request_failed",
                message = ex.Message,
                traceId
            }, context.RequestAborted);
        }
    }
}
