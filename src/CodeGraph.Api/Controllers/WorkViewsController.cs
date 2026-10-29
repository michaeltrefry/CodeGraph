using System.Globalization;
using System.Text.Json;
using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

/// <summary>
/// Read-only listings the work UI needs and the tools do not give (sc-27874): the sessions of a
/// project, its resources with holders and waiters, and its messages by channel. They write nothing
/// (not a heartbeat, not a delivery state), so they are for people signed in to the web app only;
/// agents have the briefing, the inbox and the tools. Every write the UI makes goes through
/// <see cref="WorkController"/>, the routes the agents use.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = CodeGraphAuthenticationDefaults.Scheme)]
[Route("api/work/projects/{project}")]
public sealed class WorkViewsController(IServiceProvider services) : ControllerBase
{
    private static readonly string[] Channels =
        [MessageChannels.Item, MessageChannels.Project, MessageChannels.Feature, MessageChannels.Resource, MessageChannels.Session, MessageChannels.Human];

    /// <summary>The project's sessions, most recently seen first; live_only (default true) leaves out ended and silent ones.</summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(string project, [FromQuery(Name = "live_only")] bool? liveOnly, [FromQuery] int? limit, CancellationToken ct)
    {
        if (Rejected(["live_only", "limit"]) is { } rejected)
            return rejected;
        var (store, projectId, failure) = await ResolveProjectAsync(project, ct);
        if (failure is not null)
            return failure;

        var sessions = await store!.ListSessionsAsync(projectId, liveOnly ?? true, limit ?? WorkLimits.DefaultPageSize, ct);
        return Json(new { items = sessions });
    }

    /// <summary>The project's resources with their holders and queued waiters as of now.</summary>
    [HttpGet("resources")]
    public async Task<IActionResult> Resources(string project, CancellationToken ct)
    {
        if (Rejected([]) is { } rejected)
            return rejected;
        var (store, projectId, failure) = await ResolveProjectAsync(project, ct);
        if (failure is not null)
            return failure;

        return Json(new { items = await store!.ListResourcesAsync(projectId, ct) });
    }

    /// <summary>The project's messages, newest first: channel, item, feature, resource, open, to_human, before (page cursor) and limit as filters.</summary>
    [HttpGet("messages")]
    public async Task<IActionResult> Messages(
        string project,
        [FromQuery] string? channel,
        [FromQuery] string? item,
        [FromQuery] string? feature,
        [FromQuery] string? resource,
        [FromQuery] bool? open,
        [FromQuery(Name = "to_human")] bool? toHuman,
        [FromQuery] string? before,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (Rejected(["channel", "item", "feature", "resource", "open", "to_human", "before", "limit"]) is { } rejected)
            return rejected;
        if (channel is not null && !Channels.Contains(channel))
            return Refuse(WorkException.Invalid($"channel must be one of: {string.Join(", ", Channels)}."));
        long? beforeSequence = null;
        if (before is not null)
        {
            if (!long.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                return Refuse(WorkException.Invalid("before is not a message cursor."));
            beforeSequence = parsed;
        }

        var (store, projectId, failure) = await ResolveProjectAsync(project, ct);
        if (failure is not null)
            return failure;

        Guid? itemId = null, featureId = null;
        if (item is not null)
        {
            if (await store!.ResolveAsync(item, ct) is not { Kind: WorkEntityKind.Item } target)
                return Refuse(WorkException.NotFound($"There is no item '{item.Trim()}'."));
            itemId = target.Id;
        }

        if (feature is not null)
        {
            if (await store!.ResolveAsync(feature, ct) is not { Kind: WorkEntityKind.Feature } target)
                return Refuse(WorkException.NotFound($"There is no feature '{feature.Trim()}'."));
            featureId = target.Id;
        }

        var page = await store!.ListMessagesAsync(
            new MessageListQuery(projectId, channel, itemId, featureId, resource, open ?? false, toHuman ?? false, beforeSequence, limit ?? WorkLimits.DefaultPageSize), ct);
        return Json(new { items = page.Items, next_before = page.NextBefore });
    }

    // An unknown query parameter is refused rather than ignored, like the work tools' routes.
    private IActionResult? Rejected(string[] allowed)
    {
        foreach (var (name, values) in Request.Query)
        {
            if (!allowed.Contains(name))
                return Refuse(WorkException.Invalid($"This route has no query parameter '{name}'. Its query parameters are: {(allowed.Length == 0 ? "none" : string.Join(", ", allowed))}."));
            if (values.Count != 1)
                return Refuse(WorkException.Invalid($"Query parameter '{name}' is passed {values.Count} times."));
        }

        return null;
    }

    private async Task<(IWorkStore? Store, Guid ProjectId, IActionResult? Failure)> ResolveProjectAsync(string project, CancellationToken ct)
    {
        if (services.GetService(typeof(IWorkStore)) is not IWorkStore store)
            return (null, default, Problem(detail: new WorkStoreUnavailableException().Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Work tracking is unavailable"));
        if (await store.ResolveAsync(project, ct) is not { Kind: WorkEntityKind.Project } target)
            return (null, default, Refuse(WorkException.NotFound($"There is no project '{project.Trim()}'.")));
        return (store, target.Id, null);
    }

    private static ContentResult Json(object value) => new()
    {
        Content = JsonSerializer.Serialize(value, WorkJson.Options),
        ContentType = "application/json",
        StatusCode = StatusCodes.Status200OK
    };

    private static ContentResult Refuse(WorkException exception) => new()
    {
        Content = JsonSerializer.Serialize(new WorkError(exception.Code, exception.Message), WorkJson.Options),
        ContentType = "application/json",
        StatusCode = WorkController.StatusFor(exception.Code)
    };
}
