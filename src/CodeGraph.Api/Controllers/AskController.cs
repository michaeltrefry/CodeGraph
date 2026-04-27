using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Assistant;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/ask")]
public class AskController(
    GraphAssistant assistant,
    IAssistantRunService assistantRunService,
    IAssistantConfigurationService assistantConfigurationService,
    ILogger<AskController> logger) : Controller
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TailPollInterval = TimeSpan.FromSeconds(1);

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
        var username = GetNormalizedUsername();
        logger.LogInformation("Ask stream starting — question: {Question}", request.Question[..Math.Min(request.Question.Length, 120)]);

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        await Response.Body.FlushAsync(ct);

        int eventCount = 0;
        try
        {
            await foreach (var e in assistant.AskAsync(request.Question, request.Context, request.History, username, ct))
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

    [HttpGet("options")]
    public async Task<ActionResult<AssistantConfigurationResponse>> GetOptions(CancellationToken ct)
    {
        return Ok(await assistantConfigurationService.GetConfigurationAsync(ct));
    }

    [HttpPost("runs")]
    public async Task<ActionResult<StartAssistantRunResponse>> StartRun([FromBody] AskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question is required" });

        try
        {
            var run = await assistantRunService.CreateRunAsync(request, GetNormalizedUsername(), GetIdempotencyKey(), ct);
            var response = BuildStartResponse(run.Run);
            return run.ReusedExisting ? Ok(response) : Accepted(response);
        }
        catch (AssistantRunConflictException ex)
        {
            return Conflict(new AssistantRunConflictResponse(
                ex.Conflict.Code,
                ex.Conflict.Message,
                ex.Conflict.MismatchMessageIndex,
                ex.Conflict.ExistingRunId));
        }
    }

    [HttpGet("runs/{runId:long}")]
    public async Task<ActionResult<AssistantRunResponse>> GetRun(long runId, CancellationToken ct)
    {
        var run = await assistantRunService.GetRunAsync(runId, GetNormalizedUsername(), ct);
        return run is null ? NotFound() : Ok(run);
    }

    [HttpGet("chats")]
    public async Task<ActionResult<IReadOnlyList<AssistantChatSummaryResponse>>> ListChats(
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        return Ok(await assistantRunService.ListChatsAsync(GetNormalizedUsername(), Math.Clamp(take, 1, 50), ct));
    }

    [HttpGet("chats/{chatId}")]
    public async Task<ActionResult<AssistantChatTranscriptResponse>> GetChat(string chatId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chatId))
            return BadRequest(new { error = "chatId is required" });

        var chat = await assistantRunService.GetChatAsync(chatId.Trim(), GetNormalizedUsername(), ct);
        return chat is null ? NotFound() : Ok(chat);
    }

    [HttpPost("runs/{runId:long}/cancel")]
    public async Task<ActionResult<AssistantRunResponse>> CancelRun(long runId, CancellationToken ct)
    {
        var run = await assistantRunService.CancelRunAsync(runId, GetNormalizedUsername(), ct);
        if (run is null)
            return NotFound();

        return IsTerminalStatus(run.Status) ? Ok(run) : Accepted(run);
    }

    [HttpGet("runs/{runId:long}/events")]
    public async Task<ActionResult<AssistantRunEventsResponse>> GetEvents(
        long runId,
        [FromQuery] long? afterSequence,
        CancellationToken ct)
    {
        var replayCursor = ResolveReplayCursor(afterSequence);
        if (replayCursor is null)
            return BadRequest(new { error = "afterSequence and Last-Event-ID must match when both are supplied." });

        var response = await assistantRunService.GetEventsAsync(runId, replayCursor.Value, GetNormalizedUsername(), ct);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpGet("runs/{runId:long}/debug-exchanges")]
    public async Task<ActionResult<AssistantDebugExchangeListResponse>> GetDebugExchanges(
        long runId,
        CancellationToken ct)
    {
        var response = await assistantRunService.GetDebugExchangesAsync(
            runId,
            GetNormalizedUsername(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers["User-Agent"].FirstOrDefault(),
            ct);

        return response is null ? NotFound() : Ok(response);
    }

    [HttpGet("runs/{runId:long}/stream")]
    public async Task Stream(long runId, [FromQuery] long? afterSequence, CancellationToken ct)
    {
        var replayCursor = ResolveReplayCursor(afterSequence);
        if (replayCursor is null)
        {
            Response.StatusCode = 400;
            Response.ContentType = "application/json";
            await Response.WriteAsync("{\"error\":\"afterSequence and Last-Event-ID must match when both are supplied.\"}", ct);
            return;
        }

        var username = GetNormalizedUsername();
        var run = await assistantRunService.GetRunAsync(runId, username, ct);
        if (run is null)
        {
            Response.StatusCode = 404;
            Response.ContentType = "application/json";
            await Response.WriteAsync("{\"error\":\"Assistant run not found\"}", ct);
            return;
        }

        logger.LogInformation("Assistant run stream opened for run {RunId} after sequence {AfterSequence}",
            runId, replayCursor.Value);

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        await SseHelper.WriteHeartbeatAsync(Response, ct);
        await Response.Body.FlushAsync(ct);

        var lastSequence = replayCursor.Value;
        var lastHeartbeatAt = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var snapshot = await assistantRunService.GetEventsAsync(runId, lastSequence, username, ct);
            if (snapshot is null)
            {
                Response.StatusCode = 404;
                return;
            }

            foreach (var evt in snapshot.Events)
            {
                await SseHelper.WriteEventAsync(
                    Response,
                    evt.Type,
                    evt.Content ?? "",
                    ct,
                    CodeGraph.Models.CodeGraphJsonDefaults.CamelCase,
                    evt.Sequence.ToString(CultureInfo.InvariantCulture));
                lastSequence = evt.Sequence;
            }

            if (IsTerminalStatus(snapshot.Run.Status) && lastSequence >= snapshot.Run.LastSequence)
                return;

            if (snapshot.Events.Count > 0)
                continue;

            await Task.Delay(TailPollInterval, ct);
            if (DateTime.UtcNow - lastHeartbeatAt >= HeartbeatInterval)
            {
                await SseHelper.WriteHeartbeatAsync(Response, ct);
                lastHeartbeatAt = DateTime.UtcNow;
            }
        }
    }

    private async Task WriteSseEventAsync(string type, string content, CancellationToken ct)
    {
        await SseHelper.WriteEventAsync(Response, type, content, ct);
    }

    private StartAssistantRunResponse BuildStartResponse(AssistantRunResponse run) =>
        new(
            run.Id,
            run.ChatId,
            run.Status,
            $"/api/ask/runs/{run.Id}/stream",
            $"/api/ask/runs/{run.Id}/events",
            $"/api/ask/runs/{run.Id}");

    private long? ResolveReplayCursor(long? afterSequence)
    {
        var cursorFromQuery = afterSequence ?? 0;
        if (!Request.Headers.TryGetValue("Last-Event-ID", out var headerValues) ||
            string.IsNullOrWhiteSpace(headerValues.ToString()))
            return cursorFromQuery;

        if (!long.TryParse(headerValues.ToString(), out var headerCursor))
            return null;

        if (headerCursor < 0)
            return null;

        if (afterSequence.HasValue && headerCursor < afterSequence.Value)
            return null;

        return Math.Max(cursorFromQuery, headerCursor);
    }

    private string GetNormalizedUsername() =>
        (User.GetUsername() ?? Request.Headers["X-CodeGraph-User"].FirstOrDefault() ?? "anonymous")
        .Trim()
        .ToLowerInvariant();

    private string? GetIdempotencyKey() =>
        Request.Headers.TryGetValue("Idempotency-Key", out var values)
            ? values.ToString()
            : null;

    private static bool IsTerminalStatus(string status) =>
        status is "completed" or "failed" or "cancelled" or "interrupted";
}
