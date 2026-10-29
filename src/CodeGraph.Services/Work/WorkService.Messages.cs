using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

// post, inbox and ack (sc-27871): argument rules before the database, the inbox cursor and its
// long-poll, and the unread count every session-bearing response carries.
public sealed partial class WorkService
{
    private const string InboxCursorKind = "ib";
    private static readonly TimeSpan InboxPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Adds <c>unread</c> (messages delivered to the session and not acknowledged yet) to a response
    /// when there are any; empty sections are left out (E4). One indexed count per call.
    /// </summary>
    private async Task<object> WithUnreadAsync(object result, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        session ??= await Store.GetSessionAsync(Guid.Parse(actor.SessionId!), ct);
        if (session is null)
            return result;

        var unread = await Store.CountUnreadAsync(actor.SessionId!, session.ProjectId, ct);
        if (unread == 0 || JsonSerializer.SerializeToNode(result, result.GetType(), WorkJson.Options) is not JsonObject node)
            return result;

        node["unread"] = unread;
        return node;
    }

    // ---- post ------------------------------------------------------------------------------------

    private async Task<object> PostAsync(PostRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        var body = Text(request.Body, "body", WorkMessageLimits.MaxBodyLength);
        var kind = request.Kind ?? MessageKind.Info;

        // The project: named, else the item's, else the session's.
        WorkTarget? itemTarget = null;
        if (request.Item is { } itemName)
            itemTarget = await RequireAsync(itemName, "item", ct);
        WorkTarget project;
        if (request.Project is { } projectName)
            project = await RequireProjectAsync(projectName, "project", ct);
        else if (itemTarget is not null)
            project = new WorkTarget(WorkEntityKind.Project, itemTarget.ProjectId, itemTarget.ProjectId);
        else if (session is not null)
            project = new WorkTarget(WorkEntityKind.Project, session.ProjectId, session.ProjectId);
        else
            throw WorkException.Invalid("post needs project, or item, or session_id to say which project the message belongs to. Nothing was changed.");
        await RequireSessionProjectAsync(session, project.Id, ct);

        Guid? itemId = null;
        if (itemTarget is not null)
            itemId = (await RequireOfProjectAsync(request.Item!, WorkEntityKind.Item, project, "item", ct)).Id;
        Guid? featureId = request.Feature is { } feature
            ? (await RequireOfProjectAsync(feature, WorkEntityKind.Feature, project, "feature", ct)).Id
            : null;
        var resource = request.Resource is { } resourceName ? Text(resourceName, "resource", WorkMessageLimits.MaxResourceLength) : null;
        if (resource is not null && resource.Any(char.IsControl))
            throw WorkException.Invalid("resource must not contain control characters. Nothing was changed.");

        Guid? toSession = null;
        if (request.ToSession is { } sessionName)
        {
            toSession = Guid.TryParse(sessionName.Trim(), out var target)
                ? target
                : throw WorkException.Invalid($"to_session '{sessionName}' is not a session id. Nothing was changed.");
            if (toSession.ToString() == actor.SessionId)
                throw WorkException.Invalid("to_session is the caller's own session. Nothing was changed.");
        }

        var toHuman = request.ToHuman == true;
        var targets = new[] { ("feature", featureId is not null), ("resource", resource is not null), ("to_session", toSession is not null), ("to_human", toHuman) }
            .Where(entry => entry.Item2).Select(entry => entry.Item1).ToList();
        if (targets.Count > 1)
            throw WorkException.Invalid($"post takes at most one of feature, resource, to_session, to_human; this call passes {string.Join(" and ", targets)}. Nothing was changed.");
        if (itemId is not null && (featureId is not null || resource is not null))
            throw WorkException.Invalid("A message about an item (item) goes to its thread, or directly to a session or the humans; it takes no feature or resource. Nothing was changed.");

        var blocking = request.Blocking == true;
        if (blocking && (kind != MessageKind.Question || !toHuman || itemId is null))
            throw WorkException.Invalid("blocking is for a question (kind) to the human (to_human) about an item (item): that is what marks the item Blocked. Nothing was changed.");

        Guid? replyTo = null;
        if (request.InReplyTo is { } replyName)
            replyTo = Guid.TryParse(replyName.Trim(), out var parent)
                ? parent
                : throw WorkException.Invalid($"in_reply_to '{replyName}' is not a message id. Nothing was changed.");
        if (request.Resolves is not null && replyTo is null)
            throw WorkException.Invalid("resolves closes the question or request named in in_reply_to; pass in_reply_to with it. Nothing was changed.");

        var keyHash = request.IdempotencyKey is { } key ? HashKey(actor, key) : null;
        var posted = await Store.PostMessageAsync(
            new NewMessageSpec(project.Id, body, kind, itemId, featureId, resource, toSession, toHuman, blocking, replyTo, request.Resolves), actor, keyHash, ct);
        return new PostResult(posted.Created, posted.Message, posted.Recipients, posted.Resolved);
    }

    // ---- inbox -----------------------------------------------------------------------------------

    /// <summary>The project a message read or ack is about, and who reads: the session, or the project's humans.</summary>
    private async Task<(Guid ProjectId, string Recipient)> InboxScopeAsync(string? projectName, WorkActor actor, SessionView? session, string tool, CancellationToken ct)
    {
        if (actor.SessionId is { } sessionId)
        {
            var own = session ?? throw WorkException.NotFound($"There is no session {sessionId}.");
            if (projectName is not null)
            {
                var named = await RequireProjectAsync(projectName, "project", ct);
                await RequireSessionProjectAsync(session, named.Id, ct);
            }

            return (own.ProjectId, sessionId);
        }

        if (projectName is null)
            throw WorkException.Invalid($"{tool} needs project when session_id is left out: without a session it acts for the project's humans. Nothing was changed.");
        return ((await RequireProjectAsync(projectName, "project", ct)).Id, MessageChannels.HumanRecipient);
    }

    private async Task<object> InboxAsync(InboxRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        var limit = request.Limit ?? WorkMessageLimits.DefaultInboxLimit;
        if (limit is < 1 or > WorkMessageLimits.MaxInboxLimit)
            throw WorkException.Invalid($"limit must be 1 to {WorkMessageLimits.MaxInboxLimit}.");
        var wait = request.WaitSeconds ?? 0;
        if (wait is < 0 or > WorkLimits.MaxWaitSeconds)
            throw WorkException.Invalid($"wait_seconds must be 0 to {WorkLimits.MaxWaitSeconds}.");

        var (projectId, recipient) = await InboxScopeAsync(request.Project, actor, session, WorkTools.Inbox, ct);
        var after = 0L;
        if (request.Cursor is { } cursor)
        {
            if (!long.TryParse(DecodeCursor(cursor, InboxCursorKind, "inbox"), NumberStyles.None, CultureInfo.InvariantCulture, out after))
                throw WorkException.Invalid("cursor is not a cursor of inbox; pass next_cursor back unchanged.");
            if (after > await Store.GetInboxHeadAsync(ct))
                throw WorkException.Invalid("cursor is ahead of the newest message of the store (the database was restored to an earlier state). Start again without cursor.");
        }

        var deadline = clock.GetUtcNow().AddSeconds(wait);
        while (true)
        {
            // Each poll opens and closes its own connection: a waiting caller holds none between polls.
            var page = await Store.ReadInboxAsync(new InboxQuery(projectId, recipient, after, request.IncludeRead == true, limit), ct);
            if (page.Items.Count > 0 || clock.GetUtcNow() >= deadline)
                return new InboxResult(page.Items, EncodeCursor(InboxCursorKind, page.LastSequence.ToString(CultureInfo.InvariantCulture)));

            await Task.Delay(InboxPollInterval, clock, ct);
        }
    }

    // ---- ack -------------------------------------------------------------------------------------

    private async Task<object> AckAsync(AckRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        if ((request.Messages is null) == (request.UpTo is null))
            throw WorkException.Invalid("ack takes exactly one of messages (message ids) and up_to (a next_cursor of inbox). Nothing was changed.");
        if (request.Resolution is not null && request.Messages is null)
            throw WorkException.Invalid("resolution closes the questions or requests named in messages; pass messages with it. Nothing was changed.");

        var (projectId, recipient) = await InboxScopeAsync(request.Project, actor, session, WorkTools.Ack, ct);
        IReadOnlyList<Guid>? messages = null;
        long? upTo = null;
        if (request.Messages is { } ids)
        {
            if (ids.Count is 0 or > WorkMessageLimits.MaxAckMessages)
                throw WorkException.Invalid($"messages must hold 1 to {WorkMessageLimits.MaxAckMessages} ids; it holds {ids.Count}. Nothing was changed.");
            messages = ids.Select((id, index) => Guid.TryParse(id.Trim(), out var parsed)
                ? parsed
                : throw WorkException.Invalid($"messages[{index}] '{id}' is not a message id. Nothing was changed.")).ToList();
        }
        else
        {
            if (!long.TryParse(DecodeCursor(request.UpTo!, InboxCursorKind, "inbox"), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
                throw WorkException.Invalid("up_to is not a cursor of inbox; pass a next_cursor of inbox unchanged.");
            if (cursor > await Store.GetInboxHeadAsync(ct))
                throw WorkException.Invalid("up_to is ahead of the newest message of the store (the database was restored to an earlier state). Pass a next_cursor of inbox.");
            upTo = cursor;
        }

        return await Store.AckAsync(new AckSpec(projectId, recipient, messages, upTo, request.Resolution), actor, ct);
    }
}
