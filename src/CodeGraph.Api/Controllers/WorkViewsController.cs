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
/// project, its resources with holders and waiters, its messages by channel, and an item's GitHub
/// evidence and change feed. They write nothing (not a heartbeat, not a delivery state), so they are
/// for people signed in to the web app only; agents have the briefing, the inbox and the tools.
/// Every write the UI makes goes through <see cref="WorkController"/>, the routes the agents use.
/// Query parameters are read as strictly as the tools' routes: an unknown, repeated, empty or
/// mistyped one is refused in the {code, message} envelope, and a limit out of range is refused,
/// not clamped.
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
    public Task<IActionResult> Sessions(string project, CancellationToken ct) => GuardedAsync(async () =>
    {
        var query = ReadQuery(["live_only", "limit"]);
        var liveOnly = Flag(query, "live_only") ?? true;
        var limit = Limit(query, WorkLimits.MaxPageSize);
        var (store, projectId) = await ResolveProjectAsync(project, ct);
        return Json(new { items = await store.ListSessionsAsync(projectId, liveOnly, limit, ct) });
    });

    /// <summary>The project's resources by name with their holders and queued waiters as of now: limit and after (a resource name) page it.</summary>
    [HttpGet("resources")]
    public Task<IActionResult> Resources(string project, CancellationToken ct) => GuardedAsync(async () =>
    {
        var query = ReadQuery(["limit", "after"]);
        var limit = Limit(query, WorkLimits.MaxPageSize);
        var after = query.GetValueOrDefault("after");
        if (after is not null && after.Length == 0)
            throw WorkException.Invalid("after is empty; pass the next_after of the previous page, or leave it out.");
        var (store, projectId) = await ResolveProjectAsync(project, ct);
        var page = await store.ListResourcesAsync(projectId, after, limit + 1, ct);
        return Json(new { items = page.Take(limit).ToList(), next_after = page.Count > limit ? page[limit - 1].Name : null });
    });

    /// <summary>The project's messages, newest first: channel, item, feature, resource, open, to_human, before (page cursor) and limit as filters.</summary>
    [HttpGet("messages")]
    public Task<IActionResult> Messages(string project, CancellationToken ct) => GuardedAsync(async () =>
    {
        var query = ReadQuery(["channel", "item", "feature", "resource", "open", "to_human", "before", "limit"]);
        var channel = query.GetValueOrDefault("channel");
        if (channel is not null && !Channels.Contains(channel))
            throw WorkException.Invalid($"channel must be one of: {string.Join(", ", Channels)}.");
        long? before = null;
        if (query.TryGetValue("before", out var cursor))
            before = long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw WorkException.Invalid("before is not a message cursor.");
        var open = Flag(query, "open") ?? false;
        var toHuman = Flag(query, "to_human") ?? false;
        var limit = Limit(query, WorkLimits.MaxPageSize);

        var (store, projectId) = await ResolveProjectAsync(project, ct);
        Guid? itemId = null, featureId = null;
        if (query.TryGetValue("item", out var item))
            itemId = (await RequireAsync(store, item, WorkEntityKind.Item, projectId, ct)).Id;
        if (query.TryGetValue("feature", out var feature))
            featureId = (await RequireAsync(store, feature, WorkEntityKind.Feature, projectId, ct)).Id;
        var resource = query.GetValueOrDefault("resource");
        if (resource is not null && resource.Length == 0)
            throw WorkException.Invalid("resource is empty.");

        var page = await store.ListMessagesAsync(new MessageListQuery(projectId, channel, itemId, featureId, resource, open, toHuman, before, limit), ct);
        return Json(new { items = page.Items, next_before = page.NextBefore });
    });

    /// <summary>The newest writes of the project, oldest first: the activity feeds start from this, then follow the feed from its head.</summary>
    [HttpGet("recent-changes")]
    public Task<IActionResult> RecentChanges(string project, CancellationToken ct) => GuardedAsync(async () =>
    {
        var query = ReadQuery(["limit"]);
        var limit = Limit(query, WorkLimits.MaxPageSize);
        var (store, projectId) = await ResolveProjectAsync(project, ct);
        return Json(new { items = await store.ReadRecentChangesAsync(projectId, limit, ct) });
    });

    /// <summary>The GitHub evidence of an item (pushes with their commits, merges, reviews, comments), newest first.</summary>
    [HttpGet("items/{item}/github-evidence")]
    public Task<IActionResult> GitHubEvidence(string project, string item, CancellationToken ct) => GuardedAsync(async () =>
    {
        var query = ReadQuery(["limit"]);
        var limit = Limit(query, WorkLimits.MaxPageSize);
        var (store, projectId) = await ResolveProjectAsync(project, ct);
        var target = await RequireAsync(store, item, WorkEntityKind.Item, projectId, ct);
        return Json(new { items = await store.ListGitHubEvidenceAsync(target.Id, limit, ct) });
    });

    /// <summary>
    /// The change feed entries of one item, the newest limit of them, oldest first. The project-wide
    /// <c>changes</c> tool is a cursor feed of every write; the item timeline needs one item's history
    /// without reading the rest.
    /// </summary>
    [HttpGet("items/{item}/changes")]
    public Task<IActionResult> ItemChanges(string project, string item, CancellationToken ct) => GuardedAsync(async () =>
    {
        var query = ReadQuery(["limit"]);
        var limit = Limit(query, WorkLimits.MaxPageSize);
        var (store, projectId) = await ResolveProjectAsync(project, ct);
        var target = await RequireAsync(store, item, WorkEntityKind.Item, projectId, ct);
        return Json(new { items = await store.ReadEntityChangesAsync(target.Id, limit, ct) });
    });

    // ---- strict query reading -------------------------------------------------------------------

    private async Task<IActionResult> GuardedAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (WorkException exception)
        {
            return new ContentResult
            {
                Content = JsonSerializer.Serialize(new WorkError(exception.Code, exception.Message), WorkJson.Options),
                ContentType = "application/json",
                StatusCode = WorkController.StatusFor(exception.Code)
            };
        }
        catch (WorkStoreUnavailableException exception)
        {
            return Problem(detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Work tracking is unavailable");
        }
    }

    // An unknown or repeated query parameter is refused rather than ignored, like the work tools' routes.
    private Dictionary<string, string> ReadQuery(string[] allowed)
    {
        var values = new Dictionary<string, string>();
        foreach (var (name, found) in Request.Query)
        {
            if (!allowed.Contains(name))
                throw WorkException.Invalid($"This route has no query parameter '{name}'. Its query parameters are: {(allowed.Length == 0 ? "none" : string.Join(", ", allowed))}.");
            if (found.Count != 1)
                throw WorkException.Invalid($"Query parameter '{name}' is passed {found.Count} times.");
            values[name] = found[0]!;
        }

        return values;
    }

    private static bool? Flag(Dictionary<string, string> query, string name) =>
        !query.TryGetValue(name, out var value) ? null : bool.TryParse(value, out var flag) ? flag : throw WorkException.Invalid($"Query parameter '{name}' must be true or false.");

    private static int Limit(Dictionary<string, string> query, int maximum)
    {
        if (!query.TryGetValue("limit", out var value))
            return Math.Min(WorkLimits.DefaultPageSize, maximum);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < 1 || limit > maximum)
            throw WorkException.Invalid($"limit must be 1 to {maximum}.");
        return limit;
    }

    private async Task<(IWorkStore Store, Guid ProjectId)> ResolveProjectAsync(string project, CancellationToken ct)
    {
        if (services.GetService(typeof(IWorkStore)) is not IWorkStore store)
            throw new WorkStoreUnavailableException();
        if (await store.ResolveAsync(project, ct) is not { Kind: WorkEntityKind.Project } target)
            throw WorkException.NotFound($"There is no project '{project.Trim()}'.");
        return (store, target.Id);
    }

    private static async Task<WorkTarget> RequireAsync(IWorkStore store, string key, WorkEntityKind kind, Guid projectId, CancellationToken ct) =>
        await store.ResolveAsync(key, ct) is { } target && target.Kind == kind && target.ProjectId == projectId
            ? target
            : throw WorkException.NotFound($"There is no {WorkJson.Name(kind)} '{key.Trim()}' in this project.");

    private static ContentResult Json(object value) => new()
    {
        Content = JsonSerializer.Serialize(value, WorkJson.Options),
        ContentType = "application/json",
        StatusCode = StatusCodes.Status200OK
    };
}
