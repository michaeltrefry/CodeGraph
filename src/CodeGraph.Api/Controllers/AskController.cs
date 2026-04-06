using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CodeGraph.Models.Requests;
using CodeGraph.Services;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/ask")]
public class AskController(GraphAssistant assistant, ILogger<AskController> logger) : Controller
{
    // POST /api/ask
    // Body: { "question": "..." }
    // Response: text/event-stream — SSE events
    //   data: {"type":"text","content":"..."}
    //   data: {"type":"tool_use","content":"tool_name"}
    //   data: {"type":"done","content":""}
    //   data: {"type":"error","content":"message"}
    [HttpPost]
    public async Task Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            Response.StatusCode = 400;
            Response.ContentType = "application/json";
            await Response.WriteAsync("{\"error\":\"Question is required\"}", ct);
            return;
        }

        var sw = Stopwatch.StartNew();
        logger.LogInformation("Ask stream starting — question: {Question}", request.Question[..Math.Min(request.Question.Length, 120)]);

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        await Response.Body.FlushAsync(ct);

        int eventCount = 0;
        try
        {
            await foreach (var e in assistant.AskAsync(request.Question, request.Context, request.History, ct))
            {
                eventCount++;
                await WriteSseEventAsync(e.Type, e.Content, ct);
            }

            logger.LogInformation("Ask stream completed — {EventCount} events in {Elapsed}ms", eventCount, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Ask stream cancelled by client after {EventCount} events, {Elapsed}ms", eventCount, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Ask stream failed after {EventCount} events, {Elapsed}ms", eventCount, sw.ElapsedMilliseconds);
            await WriteSseEventAsync("error", ex.Message, ct);
        }
    }

    private async Task WriteSseEventAsync(string type, string content, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { type, content });
        var line = $"data: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await Response.Body.WriteAsync(bytes, ct);
        await Response.Body.FlushAsync(ct);
    }
}
