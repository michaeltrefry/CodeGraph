using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

/// <summary>
/// The message stream the UI subscribes to (sc-27871, §5.5 "SSE for the UI"): server-sent events for
/// one project, one per message posted, handed over, read, answered or declined, in commit order.
/// Each event's <c>id</c> is its sequence, so a browser's EventSource resumes after a dropped
/// connection with Last-Event-ID; without it (or <c>cursor</c>) the stream starts at the newest
/// event. The data is a <see cref="MessageEventView"/> as JSON (snake_case, like the work tools).
/// For people signed in to the web app; agents read their inbox with the inbox tool.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = CodeGraphAuthenticationDefaults.Scheme)]
[Route("api/work/messages/stream")]
public sealed class WorkMessageStreamController(IServiceProvider services, ILogger<WorkMessageStreamController> logger) : ControllerBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private const int BatchSize = 100;

    [HttpGet]
    public async Task Stream([FromQuery] string? project, [FromQuery] string? cursor, CancellationToken ct)
    {
        var store = services.GetService(typeof(IWorkStore)) as IWorkStore;
        if (store is null)
        {
            await RefuseAsync(StatusCodes.Status503ServiceUnavailable, "Work tracking needs the MariaDB storage provider; this deployment runs without it.", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(project))
        {
            await RefuseAsync(StatusCodes.Status400BadRequest, "project is required: the key (such as SW) or id of a Project.", ct);
            return;
        }

        var target = await store.ResolveAsync(project, ct);
        if (target is not { Kind: WorkEntityKind.Project })
        {
            await RefuseAsync(StatusCodes.Status404NotFound, $"There is no project '{project.Trim()}'.", ct);
            return;
        }

        var resumeAt = cursor ?? Request.Headers["Last-Event-ID"].FirstOrDefault();
        long after;
        if (resumeAt is null)
            after = await store.GetMessageEventHeadAsync(ct);
        else if (!long.TryParse(resumeAt, NumberStyles.None, CultureInfo.InvariantCulture, out after))
        {
            await RefuseAsync(StatusCodes.Status400BadRequest, "cursor / Last-Event-ID is not an event id.", ct);
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        await Response.Body.FlushAsync(ct);
        logger.LogInformation("Work message stream opened for project {Project} after event {After}", project.Trim(), after);

        var lastWrite = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var events = await store.ReadMessageEventsAsync(target.Id, after, BatchSize, ct);
                foreach (var entry in events)
                {
                    var data = JsonSerializer.Serialize(entry, WorkJson.Options);
                    await Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"id: {entry.Sequence}\nevent: message\ndata: {data}\n\n"), ct);
                    after = entry.Sequence;
                }

                if (events.Count > 0)
                {
                    await Response.Body.FlushAsync(ct);
                    lastWrite = DateTime.UtcNow;
                    if (events.Count == BatchSize)
                        continue;
                }
                else if (DateTime.UtcNow - lastWrite >= KeepAliveInterval)
                {
                    await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(": keepalive\n\n"), ct);
                    await Response.Body.FlushAsync(ct);
                    lastWrite = DateTime.UtcNow;
                }

                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The subscriber went away.
        }
    }

    private async Task RefuseAsync(int status, string message, CancellationToken ct)
    {
        Response.StatusCode = status;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(new { code = status == 404 ? WorkErrorCodes.NotFound : WorkErrorCodes.InvalidArgument, message }, WorkJson.Options), ct);
    }
}
