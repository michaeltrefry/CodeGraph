using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Middleware;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

/// <summary>
/// REST surface of the work tools (E8): each route runs the same tool, with the same arguments and
/// the same strict reading, as its MCP tool. Bodies are the tool's JSON arguments; a refusal is
/// <c>{code, message}</c> with a status that matches the code. Agents call it with their personal
/// access token (the identity their writes are attributed to), people with their sign-in.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = CodeGraphAuthenticationDefaults.Scheme + "," + McpPatAuthenticationDefaults.Scheme)]
[Route("api/work")]
public sealed class WorkController(IWorkService work, IMcpHubStore hub, ILogger<WorkController> logger) : ControllerBase
{
    /// <summary>create_work.</summary>
    [HttpPost]
    public Task<IActionResult> Create() => RunBodyAsync(WorkTools.CreateWork);

    /// <summary>find_work.</summary>
    [HttpPost("find")]
    public Task<IActionResult> Find() => RunBodyAsync(WorkTools.FindWork);

    /// <summary>next_work.</summary>
    [HttpPost("next")]
    public Task<IActionResult> Next() => RunBodyAsync(WorkTools.NextWork);

    /// <summary>start_session.</summary>
    [HttpPost("sessions")]
    public Task<IActionResult> StartSession() => RunBodyAsync(WorkTools.StartSession);

    /// <summary>end_session: the body holds item and handoff (or is empty); the session id is the path.</summary>
    [HttpPost("sessions/{sessionId}/end")]
    public async Task<IActionResult> EndSession(string sessionId)
    {
        if (await DeniedAsync(WorkTools.EndSession) is { } denied)
            return denied;

        JsonObject arguments;
        if (Request.ContentLength == 0 || (Request.ContentLength is null && !Request.HasJsonContentType()))
        {
            arguments = new JsonObject();
        }
        else
        {
            var (body, refusal) = await ReadBodyAsync(WorkTools.EndSession);
            if (refusal is not null)
                return refusal;
            if (body.TryGetProperty("session_id", out _))
                return Refuse(WorkException.Invalid("Pass the session id in the path, not in the body."));
            arguments = JsonObject.Create(body)!;
        }

        arguments["session_id"] = sessionId;
        return await RunAsync(WorkTools.EndSession, JsonSerializer.SerializeToElement(arguments));
    }

    /// <summary>get_briefing: budget_tokens and item as query parameters; the session id is the path.</summary>
    [HttpGet("sessions/{sessionId}/briefing")]
    public async Task<IActionResult> Briefing(string sessionId)
    {
        if (await DeniedAsync(WorkTools.GetBriefing) is { } denied)
            return denied;

        JsonObject arguments;
        try
        {
            arguments = QueryArguments(WorkTools.GetBriefing, strings: ["item"], integers: ["budget_tokens"], booleans: []);
        }
        catch (WorkException exception)
        {
            return Refuse(exception);
        }

        arguments["session_id"] = sessionId;
        return await RunAsync(WorkTools.GetBriefing, JsonSerializer.SerializeToElement(arguments));
    }

    /// <summary>acquire: a hold on a resource.</summary>
    [HttpPost("holds")]
    public Task<IActionResult> Acquire() => RunBodyAsync(WorkTools.Acquire);

    /// <summary>renew_hold: the body holds ttl_seconds (or is empty); the hold id is the path.</summary>
    [HttpPost("holds/{holdId}/renew")]
    public Task<IActionResult> RenewHold(string holdId) => RunWithPathIdAsync(WorkTools.RenewHold, "hold_id", holdId);

    /// <summary>release: the body is empty; the hold id is the path.</summary>
    [HttpPost("holds/{holdId}/release")]
    public Task<IActionResult> ReleaseHold(string holdId) => RunWithPathIdAsync(WorkTools.Release, "hold_id", holdId);

    /// <summary>changes: project, cursor, from_start, limit, wait_seconds (and session_id) as query parameters.</summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes()
    {
        if (await DeniedAsync(WorkTools.Changes) is { } denied)
            return denied;

        JsonObject arguments;
        try
        {
            arguments = QueryArguments(
                WorkTools.Changes,
                strings: ["project", "cursor", "session_id"],
                integers: ["limit", "wait_seconds"],
                booleans: ["from_start"]);
        }
        catch (WorkException exception)
        {
            return Refuse(exception);
        }

        return await RunAsync(WorkTools.Changes, JsonSerializer.SerializeToElement(arguments));
    }

    /// <summary>get_work: the id is the path; session_id may be a query parameter.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        if (await DeniedAsync(WorkTools.GetWork) is { } denied)
            return denied;

        JsonObject arguments;
        try
        {
            arguments = QueryArguments(WorkTools.GetWork, strings: ["session_id"], integers: [], booleans: []);
        }
        catch (WorkException exception)
        {
            return Refuse(exception);
        }

        arguments["id"] = id;
        return await RunAsync(WorkTools.GetWork, JsonSerializer.SerializeToElement(arguments));
    }

    /// <summary>update_work: the body holds every argument but id, which is the path.</summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id)
    {
        if (await DeniedAsync(WorkTools.UpdateWork) is { } denied)
            return denied;

        var (body, refusal) = await ReadBodyAsync(WorkTools.UpdateWork);
        if (refusal is not null)
            return refusal;

        if (body.TryGetProperty("id", out _))
            return Refuse(WorkException.Invalid("Pass the id in the path, not in the body."));
        var arguments = JsonObject.Create(body)!;
        arguments["id"] = id;
        return await RunAsync(WorkTools.UpdateWork, JsonSerializer.SerializeToElement(arguments));
    }

    // A tool whose id is in the path and whose other arguments (if any) are the body, which may be empty.
    private async Task<IActionResult> RunWithPathIdAsync(string tool, string argument, string id)
    {
        if (await DeniedAsync(tool) is { } denied)
            return denied;

        JsonObject arguments;
        if (Request.ContentLength == 0 || (Request.ContentLength is null && !Request.HasJsonContentType()))
        {
            arguments = new JsonObject();
        }
        else
        {
            var (body, refusal) = await ReadBodyAsync(tool);
            if (refusal is not null)
                return refusal;
            if (body.TryGetProperty(argument, out _))
                return Refuse(WorkException.Invalid($"Pass {argument} in the path, not in the body."));
            arguments = JsonObject.Create(body)!;
        }

        arguments[argument] = id;
        return await RunAsync(tool, JsonSerializer.SerializeToElement(arguments));
    }

    private async Task<IActionResult> RunBodyAsync(string tool)
    {
        if (await DeniedAsync(tool) is { } denied)
            return denied;

        var (body, refusal) = await ReadBodyAsync(tool);
        return refusal ?? await RunAsync(tool, body);
    }

    // The request body as the tool's arguments. Read here, not bound, so that an empty or malformed
    // body, a repeated member or a wrong content type is invalid_argument in the {code, message}
    // envelope rather than a framework problem response.
    private async Task<(JsonElement Body, IActionResult? Refusal)> ReadBodyAsync(string tool)
    {
        if (!Request.HasJsonContentType())
            return (default, Refuse(WorkException.Invalid($"The body of {tool} must be sent as application/json."), StatusCodes.Status415UnsupportedMediaType));

        try
        {
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer, HttpContext.RequestAborted);
            return (WorkJson.ReadObject(buffer.ToArray(), tool), null);
        }
        catch (WorkException exception)
        {
            return (default, Refuse(exception));
        }
    }

    // A call with a personal access token is subject to the hub's access predicate for the tool the
    // route runs, exactly as the same call over /mcp is (tool enabled and available, provider
    // enabled, the token entitled to it). A person signed in through the web app is not.
    private async Task<IActionResult?> DeniedAsync(string tool)
    {
        var identity = User.Identities.FirstOrDefault(candidate =>
            candidate.IsAuthenticated && candidate.AuthenticationType == McpPatAuthenticationDefaults.Scheme);
        if (identity is null)
            return null;

        long? tokenId = long.TryParse(identity.FindFirst("mcp_pat_token_id")?.Value, out var parsed) ? parsed : null;
        var denial = await McpToolAccess.EvaluateAsync(
            hub, tool, tokenId, requiresPersonalAccessToken: true, logger, HttpContext.RequestAborted);
        return denial is null
            ? null
            : new ContentResult
            {
                Content = JsonSerializer.Serialize(new { code = denial.Code, message = denial.Message }, WorkJson.Options),
                ContentType = "application/json",
                StatusCode = StatusCodes.Status403Forbidden
            };
    }

    private async Task<IActionResult> RunAsync(string tool, JsonElement arguments)
    {
        try
        {
            var result = await work.ExecuteAsync(tool, arguments, WorkService.ActorFor(User), HttpContext.RequestAborted);
            return new ContentResult
            {
                Content = JsonSerializer.Serialize(result, result.GetType(), WorkJson.Options),
                ContentType = "application/json",
                StatusCode = StatusCodes.Status200OK
            };
        }
        catch (WorkException exception)
        {
            return Refuse(exception);
        }
        catch (WorkStoreUnavailableException exception)
        {
            // Not a work error: no code of the vocabulary, a plain problem response.
            return Problem(detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Work tracking is unavailable");
        }
    }

    public static int StatusFor(string code) => code switch
    {
        WorkErrorCodes.InvalidArgument => StatusCodes.Status400BadRequest,
        WorkErrorCodes.NotFound => StatusCodes.Status404NotFound,
        WorkErrorCodes.Conflict or WorkErrorCodes.AlreadyClaimed or WorkErrorCodes.Cycle or WorkErrorCodes.ClaimLost => StatusCodes.Status409Conflict,
        WorkErrorCodes.Busy => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    private static ContentResult Refuse(WorkException exception, int? status = null) => new()
    {
        Content = JsonSerializer.Serialize(new WorkError(exception.Code, exception.Message, exception.Current, exception.Reason), WorkJson.Options),
        ContentType = "application/json",
        StatusCode = status ?? StatusFor(exception.Code)
    };

    // The query as tool arguments: an unknown parameter is refused like an unknown argument, and a
    // value that is not of its type is refused rather than dropped.
    private JsonObject QueryArguments(string tool, string[] strings, string[] integers, string[] booleans)
    {
        var arguments = new JsonObject();
        foreach (var (name, values) in Request.Query)
        {
            if (values.Count != 1)
                throw WorkException.Invalid($"Query parameter '{name}' is passed {values.Count} times.");
            var value = values[0]!;
            if (strings.Contains(name))
                arguments[name] = value;
            else if (integers.Contains(name))
                arguments[name] = int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    ? number
                    : throw WorkException.Invalid($"Query parameter '{name}' must be an integer.");
            else if (booleans.Contains(name))
                arguments[name] = bool.TryParse(value, out var flag) ? flag : throw WorkException.Invalid($"Query parameter '{name}' must be true or false.");
            else
                throw WorkException.Invalid($"{tool} has no query parameter '{name}'. Its query parameters are: {string.Join(", ", strings.Concat(integers).Concat(booleans))}.");
        }

        return arguments;
    }
}
