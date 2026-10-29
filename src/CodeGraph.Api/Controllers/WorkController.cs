using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Api.Auth;
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
public sealed class WorkController(IWorkService work) : ControllerBase
{
    /// <summary>create_work.</summary>
    [HttpPost]
    public Task<IActionResult> Create([FromBody] JsonElement body) => RunAsync(WorkTools.CreateWork, body);

    /// <summary>find_work.</summary>
    [HttpPost("find")]
    public Task<IActionResult> Find([FromBody] JsonElement body) => RunAsync(WorkTools.FindWork, body);

    /// <summary>next_work.</summary>
    [HttpPost("next")]
    public Task<IActionResult> Next([FromBody] JsonElement body) => RunAsync(WorkTools.NextWork, body);

    /// <summary>changes: project, cursor, from_start, limit, wait_seconds as query parameters.</summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes()
    {
        JsonObject arguments;
        try
        {
            arguments = QueryArguments(
                strings: ["project", "cursor"],
                integers: ["limit", "wait_seconds"],
                booleans: ["from_start"]);
        }
        catch (WorkException exception)
        {
            return Refuse(exception);
        }

        return await RunAsync(WorkTools.Changes, JsonSerializer.SerializeToElement(arguments));
    }

    /// <summary>get_work.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        if (Request.Query.Count > 0)
            return Refuse(WorkException.Invalid($"get_work takes no query parameters ({string.Join(", ", Request.Query.Keys)}). Its only argument is the id in the path."));
        return await RunAsync(WorkTools.GetWork, JsonSerializer.SerializeToElement(new JsonObject { ["id"] = id }));
    }

    /// <summary>update_work: the body holds every argument but id, which is the path.</summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Refuse(WorkException.Invalid("The body of update_work must be a JSON object."));
        var arguments = JsonObject.Create(body)!;
        if (arguments.ContainsKey("id"))
            return Refuse(WorkException.Invalid("Pass the id in the path, not in the body."));
        arguments["id"] = id;
        return await RunAsync(WorkTools.UpdateWork, JsonSerializer.SerializeToElement(arguments));
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
    }

    public static int StatusFor(string code) => code switch
    {
        WorkErrorCodes.InvalidArgument => StatusCodes.Status400BadRequest,
        WorkErrorCodes.NotFound => StatusCodes.Status404NotFound,
        WorkErrorCodes.Conflict or WorkErrorCodes.AlreadyClaimed or WorkErrorCodes.Cycle or WorkErrorCodes.ClaimLost => StatusCodes.Status409Conflict,
        WorkErrorCodes.Busy or WorkErrorCodes.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    private static ContentResult Refuse(WorkException exception) => new()
    {
        Content = JsonSerializer.Serialize(new WorkError(exception.Code, exception.Message, exception.Current), WorkJson.Options),
        ContentType = "application/json",
        StatusCode = StatusFor(exception.Code)
    };

    // The query as tool arguments: an unknown parameter is refused like an unknown argument, and a
    // value that is not of its type is refused rather than dropped.
    private JsonObject QueryArguments(string[] strings, string[] integers, string[] booleans)
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
                throw WorkException.Invalid($"changes has no argument '{name}'. Its arguments are: {string.Join(", ", strings.Concat(integers).Concat(booleans))}.");
        }

        return arguments;
    }
}
