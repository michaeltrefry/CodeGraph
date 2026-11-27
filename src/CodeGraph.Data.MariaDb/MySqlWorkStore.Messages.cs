using System.Text;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Durable messages (sc-27871): posts, per-recipient deliveries, replay, acknowledgement, answers,
// handover of open questions and requests to the next holder of an item, the inheritance of direct
// messages by the next session, and the event stream of the UI.
//
// Ordering. Every INSERT into work_messages / work_message_deliveries / work_message_events and the
// COMMIT run while the writer holds the named lock MessageLockName, so a higher sequence is always
// committed later and an inbox cursor never skips a row (the idea of the change feed's lock).
// Lock order, everywhere: the project's row, then rows of work_items (in id order), then (where a
// call touches resources) work_resources and work_holds, then MessageLockName, then the change-feed
// lock (the commit of an entry). The attempt rows of a claim are written before the messages lock. Code holding MessageLockName
// takes no further row lock on work_items, work_projects or work_sessions (it reads them without
// locking, or on a second connection), and the tables of messages have no foreign keys, so no
// writer waits for a row while another waits for the lock. Every taker acquires the lock inside its
// transaction through a MessageLockScope, which releases it after the commit.
//
// A post to an item's thread holds that item's row (shared, or exclusive when it opens or closes a
// blocking question) before the lock. A claim holds the item's row exclusively. So a claim either
// commits before a post (the post then sees the new holder) or after it (the claim's handover sees
// the post), which is what lets a claim skip the lock when an unlocked look finds nothing to hand over.
public sealed partial class MySqlWorkStore
{
    public const string MessageLockName = "codegraph_work_messages";
    private const string MessageOperation = "message";

    // The condition, on alias i, for an item with an open question to the human that blocks it.
    private const string OpenBlockingQuestion =
        "(i.status NOT IN ('complete', 'closed') AND EXISTS (SELECT 1 FROM work_messages bq WHERE bq.item_id = i.id AND bq.blocking = 1 AND bq.state = 'open'))";

    private static string RecipientOf(WorkActor actor) => actor.SessionId ?? MessageChannels.HumanRecipient;

    /// <summary>The messages lock taken inside a transaction, after its row locks, and released after its commit.</summary>
    private sealed class MessageLockScope(MySqlConnection connection, MySqlTransaction transaction) : IAsyncDisposable
    {
        private bool held;

        public async Task AcquireAsync(CancellationToken ct)
        {
            if (held)
                return;
            // Set first, so that RELEASE_LOCK follows even when the call is aborted after the server granted the lock.
            held = true;
            if (!await TryGetLockAsync(connection, MessageLockName, ct, transaction))
            {
                held = false;
                throw WorkException.Busy($"Other messages were being recorded for {LockTimeoutSeconds} s. Nothing was changed; try again.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!held)
                return;
            held = false;
            await ReleaseLockAsync(connection, MessageLockName, transaction);
        }
    }

    // ---- Rows and views --------------------------------------------------------------------------

    private sealed class MessageRow
    {
        public long Sequence { get; init; }
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string ProjectKey { get; init; } = "";
        public string Channel { get; init; } = "";
        public Guid? ItemId { get; init; }
        public Guid? FeatureId { get; init; }
        public string Kind { get; init; } = "";
        public string? State { get; init; }
        public bool Blocking { get; init; }
        public string? ToSessionId { get; init; }
        public bool ToHuman { get; init; }
        public string? ResourceName { get; init; }
        public Guid? InReplyTo { get; init; }
        public string Body { get; init; } = "";
        public string SenderName { get; init; } = "";
        public string SenderKind { get; init; } = "";
        public string? SenderSessionId { get; init; }
        public string? SenderHarness { get; init; }
        public string? SenderModel { get; init; }
        public string? SenderHost { get; init; }
        public string? ResolvedBy { get; init; }
        public string? ResolvedByKind { get; init; }
        public string? ResolvedBySessionId { get; init; }
        public string? ResolverHarness { get; init; }
        public string? ResolverModel { get; init; }
        public string? ResolverHost { get; init; }
        public DateTime? ResolvedAt { get; init; }
        public bool ResolutionRelayedFromChat { get; init; }
        public Guid? ResolutionMessageId { get; init; }
        public string? Answer { get; init; }
        public DateTime CreatedAt { get; init; }
        public int? ItemNumber { get; init; }
        public int? FeatureNumber { get; init; }
        public long? DeliverySequence { get; init; }
        public string? DeliveryState { get; init; }
        public bool? DeliveryHandedOver { get; init; }
    }

    private const string MessageColumns =
        "m.sequence, m.id, m.project_id, p.project_key, m.channel, m.item_id, m.feature_id, m.kind, m.state, m.blocking, m.to_session_id, m.to_human, m.resource_name, " +
        "m.in_reply_to, m.body, m.sender_name, m.sender_kind, m.sender_session_id, COALESCE(ss.harness, m.sender_harness) AS sender_harness, COALESCE(ss.model, m.sender_model) AS sender_model, COALESCE(ss.host, m.sender_host) AS sender_host, " +
        "m.resolved_by, m.resolved_by_kind, m.resolved_by_session_id, COALESCE(rs.harness, m.resolved_by_harness) AS resolver_harness, COALESCE(rs.model, m.resolved_by_model) AS resolver_model, COALESCE(rs.host, m.resolved_by_host) AS resolver_host, " +
        "m.resolved_at, m.resolution_relayed_from_chat, m.resolution_message_id, am.body AS answer, m.created_at, i.number AS item_number, f.number AS feature_number";

    private const string DeliveryColumns = ", d.sequence AS delivery_sequence, d.state AS delivery_state, d.handed_over AS delivery_handed_over";

    private const string MessageJoins =
        " JOIN work_projects p ON p.id = m.project_id LEFT JOIN work_items i ON i.id = m.item_id LEFT JOIN work_features f ON f.id = m.feature_id " +
        "LEFT JOIN work_sessions ss ON ss.id = m.sender_session_id LEFT JOIN work_sessions rs ON rs.id = m.resolved_by_session_id " +
        "LEFT JOIN work_messages am ON am.id = m.resolution_message_id";

    private static Task<MessageRow?> ReadMessageRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid messageId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<MessageRow>(new CommandDefinition(
            $"SELECT {MessageColumns} FROM work_messages m{MessageJoins} WHERE m.id = @messageId",
            new { messageId }, transaction, cancellationToken: ct));

    private static MessageView ToView(MessageRow row)
    {
        var item = row.ItemNumber is { } itemNumber ? WorkKeys.Item(row.ProjectKey, itemNumber) : null;
        var feature = row.FeatureNumber is { } featureNumber ? WorkKeys.Feature(row.ProjectKey, featureNumber) : null;
        return new MessageView(
            row.Id, row.ProjectKey, row.Channel, ParseName<MessageKind>(row.Kind),
            new WorkActor(row.SenderName, row.SenderKind, row.SenderSessionId, row.SenderHarness, row.SenderModel, row.SenderHost),
            row.Body, Utc(row.CreatedAt),
            row.State is null ? null : ParseName<MessageState>(row.State),
            row.Blocking ? true : null,
            row.ToSessionId,
            row.ToHuman ? true : null,
            item, feature, row.ResourceName, row.InReplyTo,
            row.ResolvedBy is null ? null : new WorkActor(row.ResolvedBy, row.ResolvedByKind ?? WorkActor.UserKind, row.ResolvedBySessionId, row.ResolverHarness, row.ResolverModel, row.ResolverHost),
            row.ResolvedAt is { } resolvedAt ? Utc(resolvedAt) : null,
            row.DeliveryState is null ? null : ParseName<DeliveryState>(row.DeliveryState),
            row.DeliveryHandedOver == true ? true : null,
            row.ResolutionRelayedFromChat ? true : null,
            row.ResolutionMessageId,
            row.Answer);
    }

    private static MessageLine ToLine(MessageRow row)
    {
        var item = row.ItemNumber is { } itemNumber ? WorkKeys.Item(row.ProjectKey, itemNumber) : null;
        var from = row.SenderHarness is null ? row.SenderName : $"{row.SenderName} ({row.SenderHarness})";
        return new MessageLine(
            row.Id, ParseName<MessageKind>(row.Kind), from, Summary(row.Body), Utc(row.CreatedAt), row.SenderSessionId, item,
            row.State is null ? null : ParseName<MessageState>(row.State),
            row.Blocking ? true : null,
            row.DeliveryState is null ? null : ParseName<DeliveryState>(row.DeliveryState),
            row.DeliveryHandedOver == true ? true : null);
    }

    private static string Summary(string body)
    {
        var line = body.Split('\n', 2)[0].Trim();
        return line.Length <= WorkMessageLimits.SummaryLength ? line : line[..(WorkMessageLimits.SummaryLength - 1)] + "…";
    }

    // ---- Writing deliveries and events (the caller holds the messages lock) ----------------------

    private static async Task<bool> InsertDeliveryAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid messageId, Guid projectId, string recipient,
        DeliveryState state, bool handedOver, DateTime now, CancellationToken ct)
    {
        var resolved = state is DeliveryState.Answered or DeliveryState.Declined;
        var read = resolved || state == DeliveryState.Read;
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO work_message_deliveries (message_id, project_id, recipient, state, delivered_at, read_at, resolved_at, handed_over, created_at) " +
            "VALUES (@messageId, @projectId, @recipient, @state, @delivered, @read, @resolved, @handedOver, @now)",
            new
            {
                messageId, projectId, recipient, state = JsonName(state),
                delivered = state == DeliveryState.Pending ? (DateTime?)null : now,
                read = read ? now : (DateTime?)null,
                resolved = resolved ? now : (DateTime?)null,
                handedOver, now
            },
            transaction, cancellationToken: ct));
        return inserted > 0;
    }

    private static Task InsertMessageEventAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid projectId, Guid messageId, string kind, string? recipient,
        WorkActor actor, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_message_events (project_id, message_id, kind, recipient, actor_name, actor_kind, actor_session_id, created_at) " +
            "VALUES (@projectId, @messageId, @kind, @recipient, @name, @actorKind, @session, @now)",
            new { projectId, messageId, kind, recipient, name = actor.Name, actorKind = actor.Kind, session = actor.SessionId, now },
            transaction, cancellationToken: ct));

    private static async Task<WorkActor> SessionActorAsync(MySqlConnection connection, MySqlTransaction? transaction, string sessionId, string identity, CancellationToken ct)
    {
        if (Guid.TryParse(sessionId, out var id) && await ReadSessionRowAsync(connection, transaction, id, ct) is { } session)
            return ActorOf(session);
        return new WorkActor(identity, WorkActor.SessionKind, sessionId);
    }

    // ---- post ------------------------------------------------------------------------------------

    // A reply with no target of its own is routed to who the message it answers was for.
    private static bool HasExplicitTarget(NewMessageSpec spec) =>
        spec.ItemId is not null || spec.FeatureId is not null || spec.Resource is not null || spec.ToSession is not null || spec.ToHuman;

    /// <summary>
    /// Holds the rows of these items, in id order: exclusively where the call opens or closes a
    /// blocking question (it bumps the item), shared where it only writes on the item's thread (so a
    /// claim of the item cannot slip between the post and its handover).
    /// </summary>
    private static async Task HoldItemsAsync(MySqlConnection connection, MySqlTransaction transaction, IReadOnlyDictionary<Guid, bool> exclusive, CancellationToken ct)
    {
        foreach (var (id, bump) in exclusive.OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal))
        {
            if (bump)
                await connection.ExecuteAsync(new CommandDefinition("UPDATE work_items SET version = version WHERE id = @id", new { id }, transaction, cancellationToken: ct));
            else
                await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id FROM work_items WHERE id = @id LOCK IN SHARE MODE", new { id }, transaction, cancellationToken: ct));
        }
    }

    /// <summary>
    /// A blocking question to the human opened or was closed: the item's derived Blocked state
    /// changed, so the item's version moves and the change feed says so. The caller holds the item's
    /// row exclusively and the messages lock.
    /// </summary>
    private static async Task<(PendingChange? Entry, ItemVersionRef? Version)> AwaitingHumanChangeAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid itemId, bool opened, DateTime now, CancellationToken ct)
    {
        var before = (await ReadItemRowAsync(connection, transaction, itemId, ct))!;
        var open = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_messages WHERE item_id = @itemId AND blocking = 1 AND state = 'open'", new { itemId }, transaction, cancellationToken: ct));
        // The derived state, as OpenBlockingQuestion reads it: a finished item is never awaiting the human.
        var finished = WorkStatuses.IsFinished(WorkStatuses.Parse(before.Status));
        var wasAwaiting = !finished && (opened ? open - 1 : open + 1) > 0;
        var isAwaiting = !finished && open > 0;
        if (wasAwaiting == isAwaiting)
            return (null, null);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_items SET version = version + 1, updated_at = @now WHERE id = @itemId", new { itemId, now }, transaction, cancellationToken: ct));
        var after = (await ReadItemRowAsync(connection, transaction, itemId, ct))!;
        var changes = new JsonArray
        {
            Change("awaiting_human", wasAwaiting, isAwaiting),
            Change("version", before.Version, after.Version)
        };
        return (new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Item, after.Id, after.Key, WorkChangeKinds.ItemUpdated, changes),
            new ItemVersionRef(after.Key, after.Version));
    }

    private static void Collect((PendingChange? Entry, ItemVersionRef? Version) change, List<PendingChange> entries, List<ItemVersionRef> versions)
    {
        if (change.Entry is not null)
            entries.Add(change.Entry);
        if (change.Version is not null)
            versions.Add(change.Version);
    }

    public Task<PostedMessage> PostMessageAsync(NewMessageSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            DreamPolicy.Refuse(actor, "PostMessageAsync");
            var now = Now();
            // Which item rows this call needs, known before anything is locked: the item's message
            // rows and the parent's item never change.
            var parentBefore = spec.InReplyTo is { } parentName ? await ReadMessageRowAsync(connection, null, parentName, ct) : null;
            var threadItem = spec.ItemId ?? (parentBefore is not null && !HasExplicitTarget(spec) ? parentBefore.ItemId : null);
            var holds = new Dictionary<Guid, bool>();
            if (threadItem is { } thread)
                holds[thread] = spec.Blocking;
            if (spec.Resolves is not null && parentBefore is { Blocking: true, ItemId: { } blockedItem })
                holds[blockedItem] = true;
            // A reply may fall back to the holder of the question's item: hold that row too, so a claim
            // either sees this reply (handover) or commits after it.
            if (parentBefore is { SenderSessionId: not null, ItemId: { } askedItem })
                holds.TryAdd(askedItem, false);

            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var messageLock = new MessageLockScope(connection, transaction);
            await HoldItemsAsync(connection, transaction, holds, ct);
            // The first consistent read comes after the lock, so it sees every claim, post and answer committed before it.
            await messageLock.AcquireAsync(ct);
            // The locks may have been waited for: liveness of holders and sessions is judged from here.
            now = Now();
            if (keyHash is not null && await FindKeyAsync(connection, transaction, spec.ProjectId, MessageOperation, keyHash, ct) is { EntityId: { } existing })
            {
                var earlier = (await ReadMessageRowAsync(connection, transaction, existing, ct))!;
                var recipients = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM work_message_deliveries WHERE message_id = @existing AND handed_over = 0", new { existing }, transaction, cancellationToken: ct));
                await transaction.CommitAsync(ct);
                return new PostedMessage(false, ToView(earlier), recipients, null);
            }

            if (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM work_projects WHERE id = @projectId", new { spec.ProjectId }, transaction, cancellationToken: ct)) == 0)
                throw WorkException.NotFound($"There is no project {spec.ProjectId}.");

            // A reply without a target goes to who the message it answers was for: its sender when that
            // is another session; otherwise (the caller's own message, or one a person sent) the
            // recipients the original named, so neither a project broadcast nor the humans' inbox again.
            MessageRow? parent = null;
            var itemId = spec.ItemId;
            var featureId = spec.FeatureId;
            var resource = spec.Resource;
            var toSession = spec.ToSession?.ToString();
            var toHuman = spec.ToHuman;
            if (spec.InReplyTo is { } parentId)
            {
                parent = await ReadMessageRowAsync(connection, transaction, parentId, ct);
                if (parent is null || parent.ProjectId != spec.ProjectId)
                    throw WorkException.NotFound($"There is no message {parentId} in this project. Nothing was changed.");
                if (spec.RelayedFromChat)
                {
                    if (!actor.IsAgent || !parent.ToHuman || parent.Kind != "question")
                        throw WorkException.Invalid("relayed_from_chat is for an agent relaying a human's chat decision on a question addressed to the humans. Nothing was changed.");
                    if (itemId is not null && itemId != parent.ItemId)
                        throw WorkException.Invalid("item must match the question's item when relaying a chat decision. Nothing was changed.");
                }
                else if (actor.IsAgent && parent.ToHuman && spec.Resolves is not null)
                    throw new WorkException(WorkErrorCodes.Forbidden,
                        "An agent cannot directly resolve a question or request addressed to the humans. If the human explicitly answered or declined the question in chat, " +
                        "reply with relayed_from_chat: true, in_reply_to, resolves and the decision in body. Reply without resolves to add information. Nothing was changed.");
                if (spec.RelayedFromChat || !HasExplicitTarget(spec))
                {
                    itemId = parent.ItemId;
                    if (parent.SenderSessionId is { } sender && sender != actor.SessionId)
                    {
                        toSession = sender;
                    }
                    else if (spec.RelayedFromChat || (parent.SenderSessionId is null && actor.SessionId is not null))
                    {
                        // A relay without another sender to notify stays on the human thread.
                        toHuman = true;
                    }
                    else
                    {
                        if (parent.ToSessionId is { } original && original != actor.SessionId)
                            toSession = original;
                        else if (parent.ToHuman && actor.SessionId is not null)
                            toHuman = true;
                        else if (parent.FeatureId is not null)
                            featureId = parent.FeatureId;
                        else if (parent.ResourceName is not null)
                            resource = parent.ResourceName;
                    }
                }
            }

            if (toSession is not null)
            {
                var target = Guid.TryParse(toSession, out var targetId) ? await ReadSessionRowAsync(connection, transaction, targetId, ct) : null;
                if (target is null || target.ProjectId != spec.ProjectId)
                    throw WorkException.Invalid($"to_session '{toSession}' is not a session of this project. Nothing was changed.");
            }

            var channel = toHuman ? MessageChannels.Human
                : toSession is not null ? MessageChannels.Session
                : featureId is not null ? MessageChannels.Feature
                : resource is not null ? MessageChannels.Resource
                : itemId is not null ? MessageChannels.Item
                : MessageChannels.Project;

            // Who gets a copy now. Others reach the message later: the next holder of the item
            // (handover), the next session of a project and repository (direct messages). A project
            // or resource message reaches only the sessions live now; a feature message the sessions
            // holding one of its items now (none: a message with no recipient, kept and visible).
            var recipientIds = new List<string>();
            if (toHuman)
            {
                recipientIds.Add(MessageChannels.HumanRecipient);
            }
            else if (toSession is not null)
            {
                recipientIds.Add(toSession);
            }
            else if (featureId is { } feature)
            {
                recipientIds.AddRange(await connection.QueryAsync<string>(new CommandDefinition(
                    "SELECT DISTINCT i.claim_session_id FROM work_items i WHERE i.feature_id = @feature AND i.claim_session_id IS NOT NULL " +
                    "AND i.claim_holder IS NOT NULL AND i.claim_expires_at > @now AND i.status NOT IN ('complete', 'closed')",
                    new { feature, now }, transaction, cancellationToken: ct)));
            }
            else if (resource is not null || itemId is null)
            {
                recipientIds.AddRange(await connection.QueryAsync<string>(new CommandDefinition(
                    "SELECT id FROM work_sessions WHERE project_id = @projectId AND ended_at IS NULL AND last_seen_at > @staleBefore",
                    new { projectId = spec.ProjectId, staleBefore = StaleBefore(now) }, transaction, cancellationToken: ct)));
            }
            else
            {
                if (await LiveHolderAsync(connection, transaction, itemId.Value, now, ct) is { } holder)
                    recipientIds.Add(holder);
            }

            // A reply reaches the session that sent the message it answers, whatever else it targets
            // (CG-180): the UI's answer names the item, which alone would reach only the item's holder.
            // A sender that ended or fell silent is not there to read it: the item's live holder is.
            if (parent?.SenderSessionId is { } asker && asker != actor.SessionId)
            {
                recipientIds.Add(asker);
                if (parent.ItemId is { } askedAbout && !await IsLiveSessionAsync(connection, transaction, asker, now, ct)
                    && await LiveHolderAsync(connection, transaction, askedAbout, now, ct) is { } holder)
                    recipientIds.Add(holder);
            }

            recipientIds = recipientIds.Where(recipient => recipient != actor.SessionId).Distinct(StringComparer.Ordinal).ToList();

            var id = Guid.NewGuid();
            var state = spec.Kind is MessageKind.Question or MessageKind.Request ? "open" : null;
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_messages (id, project_id, channel, item_id, feature_id, resource_name, to_session_id, to_human, kind, state, blocking, in_reply_to, body, " +
                "sender_name, sender_kind, sender_session_id, created_at) " +
                "VALUES (@id, @projectId, @channel, @itemId, @featureId, @resource, @toSession, @toHuman, @kind, @state, @blocking, @inReplyTo, @body, @name, @actorKind, @session, @now)",
                new
                {
                    id, projectId = spec.ProjectId, channel, itemId, featureId, resource, toSession, toHuman,
                    kind = JsonName(spec.Kind), state, blocking = spec.Blocking, inReplyTo = spec.InReplyTo, body = spec.Body,
                    name = actor.Name, actorKind = actor.Kind, session = actor.SessionId, now
                },
                transaction, cancellationToken: ct));
            // Keep the relayed reply in the human's inbox history without asking them to read it again.
            var deliveryState = spec.RelayedFromChat && toHuman ? DeliveryState.Read : DeliveryState.Pending;
            foreach (var recipient in recipientIds)
                await InsertDeliveryAsync(connection, transaction, id, spec.ProjectId, recipient, deliveryState, handedOver: false, now, ct);
            await InsertMessageEventAsync(connection, transaction, spec.ProjectId, id, MessageEventKinds.Posted, null, actor, now, ct);
            if (keyHash is not null)
                await InsertKeyAsync(connection, transaction, spec.ProjectId, MessageOperation, keyHash, id, null, now, ct);

            var entries = new List<PendingChange>();
            var versions = new List<ItemVersionRef>();
            if (spec.Blocking && itemId is { } blocked)
                Collect(await AwaitingHumanChangeAsync(connection, transaction, blocked, opened: true, now, ct), entries, versions);

            MessageRow? resolved = null;
            if (spec.Resolves is { } resolution)
            {
                await ResolveAsync(connection, transaction, parent!, resolution, actor, now, ct, spec.RelayedFromChat, answerId: id);
                if (parent!.Blocking && parent.ItemId is { } unblocked)
                    Collect(await AwaitingHumanChangeAsync(connection, transaction, unblocked, opened: false, now, ct), entries, versions);
                resolved = await ReadMessageRowAsync(connection, transaction, parent.Id, ct);
            }

            var row = (await ReadMessageRowAsync(connection, transaction, id, ct))!;
            await CommitAsync(connection, transaction, entries, actor, now, ct);
            return new PostedMessage(true, ToView(row), recipientIds.Count, resolved is null ? null : ToView(resolved), versions.Count == 0 ? null : versions);
        }, ct);

    /// <summary>
    /// Closes an open question or request as <paramref name="actor"/>: the message, and the actor's
    /// own delivery (made if it has none), or the human delivery for an explicit relay. The UPDATE
    /// is guarded by state = 'open', and a message closed already is refused conflict. The caller holds the messages
    /// lock (and, for a blocking question, its item's row).
    /// </summary>
    private async Task ResolveAsync(
        MySqlConnection connection, MySqlTransaction transaction, MessageRow message, MessageResolution resolution, WorkActor actor,
        DateTime now, CancellationToken ct, bool relayedFromChat = false, Guid? answerId = null)
    {
        var kind = ParseName<MessageKind>(message.Kind);
        if (kind is not (MessageKind.Question or MessageKind.Request))
            throw WorkException.Invalid($"Message {message.Id} is {JsonName(kind)}: only a question or a request is answered or declined. Nothing was changed.");

        var name = JsonName(resolution);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_messages SET state = @name, resolved_by = @by, resolved_by_kind = @byKind, resolved_by_session_id = @bySession, resolved_at = @now, " +
            "resolution_relayed_from_chat = @relayedFromChat, resolution_message_id = @answerId " +
            "WHERE id = @id AND state = 'open'",
            new { name, by = actor.Name, byKind = actor.Kind, bySession = actor.SessionId, now, relayedFromChat, answerId, id = message.Id }, transaction, cancellationToken: ct));
        if (changed == 0)
        {
            var current = await connection.QuerySingleAsync<(string? State, string? ResolvedBy)>(new CommandDefinition(
                "SELECT state, resolved_by FROM work_messages WHERE id = @id", new { id = message.Id }, transaction, cancellationToken: ct));
            throw new WorkException(WorkErrorCodes.Conflict,
                $"Message {message.Id} is already {current.State}{(current.ResolvedBy is null ? string.Empty : $" (by {current.ResolvedBy})")}. Nothing was changed.");
        }

        var recipient = relayedFromChat ? MessageChannels.HumanRecipient : RecipientOf(actor);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_message_deliveries SET state = @name, delivered_at = COALESCE(delivered_at, @now), read_at = COALESCE(read_at, @now), resolved_at = @now " +
            "WHERE message_id = @id AND recipient = @recipient",
            new { name, now, id = message.Id, recipient }, transaction, cancellationToken: ct));
        if (updated == 0)
            await InsertDeliveryAsync(connection, transaction, message.Id, message.ProjectId, recipient,
                resolution == MessageResolution.Answered ? DeliveryState.Answered : DeliveryState.Declined, handedOver: false, now, ct);
        await InsertMessageEventAsync(connection, transaction, message.ProjectId, message.Id, name, recipient, actor, now, ct);

        // Closed without a reply (ack with a resolution): the asking session learns it from the
        // question itself, delivered back to it as answered or declined (CG-180). A reply reaches it on its own.
        if (answerId is null && message.SenderSessionId is { } asker && asker != actor.SessionId)
        {
            await InsertDeliveryAsync(connection, transaction, message.Id, message.ProjectId, asker, DeliveryState.Pending, handedOver: false, now, ct);
            // An asker that ended is not there to read it: the item's live holder is.
            if (message.ItemId is { } askedAbout && !await IsLiveSessionAsync(connection, transaction, asker, now, ct)
                && await LiveHolderAsync(connection, transaction, askedAbout, now, ct) is { } holder && holder != actor.SessionId)
                await InsertDeliveryAsync(connection, transaction, message.Id, message.ProjectId, holder, DeliveryState.Pending, handedOver: false, now, ct);
        }
    }

    private async Task<bool> IsLiveSessionAsync(MySqlConnection connection, MySqlTransaction transaction, string sessionId, DateTime now, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_sessions WHERE id = @sessionId AND ended_at IS NULL AND last_seen_at > @staleBefore",
            new { sessionId, staleBefore = StaleBefore(now) }, transaction, cancellationToken: ct)) > 0;

    private static Task<string?> LiveHolderAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, DateTime now, CancellationToken ct) =>
        connection.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT claim_session_id FROM work_items WHERE id = @itemId AND claim_session_id IS NOT NULL AND claim_holder IS NOT NULL AND claim_expires_at > @now",
            new { itemId, now }, transaction, cancellationToken: ct));

    // ---- inbox -----------------------------------------------------------------------------------

    public async Task<InboxPage> ReadInboxAsync(InboxQuery query, CancellationToken ct = default)
    {
        DreamPolicy.RefuseAmbient("change inbox delivery state");
        await using var connection = await OpenAsync(ct);
        var rows = (await connection.QueryAsync<MessageRow>(new CommandDefinition(
            $"SELECT {MessageColumns}{DeliveryColumns} FROM work_message_deliveries d JOIN work_messages m ON m.id = d.message_id{MessageJoins} " +
            "WHERE d.recipient = @recipient AND d.project_id = @projectId AND d.sequence > @after" +
            (query.IncludeRead ? string.Empty : " AND d.read_at IS NULL") +
            " ORDER BY d.sequence LIMIT @limit",
            new { recipient = query.Recipient, projectId = query.ProjectId, after = query.AfterSequence, limit = query.Limit },
            cancellationToken: ct))).ToList();
        if (rows.Count == 0)
            return new InboxPage([], query.AfterSequence);

        // Delivered now; read only by ack, so a session that crashes before it replays these.
        var pending = rows.Where(row => row.DeliveryState == "pending").Select(row => row.Id).ToArray();
        if (pending.Length > 0)
            await MarkDeliveredAsync(query.Recipient, pending, ct);

        var views = rows.Select(row => ToView(row) with { Delivery = row.DeliveryState == "pending" ? DeliveryState.Delivered : ParseName<DeliveryState>(row.DeliveryState!) }).ToList();
        return new InboxPage(views, rows[^1].DeliverySequence!.Value);
    }

    // Under the deadlock retry like every write: a victim is run again, then refused busy.
    public async Task MarkDeliveredAsync(string recipient, IReadOnlyList<Guid> messageIds, CancellationToken ct = default)
    {
        DreamPolicy.RefuseAmbient("mark message deliveries");
        if (messageIds.Count == 0)
            return;
        var now = Now();
        await RunAsync([], connection => connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_message_deliveries SET state = 'delivered', delivered_at = @now " +
            "WHERE recipient = @recipient AND message_id IN @messageIds AND state = 'pending'",
            new { recipient, messageIds, now }, cancellationToken: ct)), ct);
    }

    public async Task<long> GetInboxHeadAsync(CancellationToken ct = default)
    {
        DreamPolicy.RefuseAmbient("read the global inbox head");
        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT MAX(sequence) FROM work_message_deliveries", cancellationToken: ct)) ?? 0;
    }

    public async Task<int> CountUnreadAsync(string recipient, Guid projectId, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var connection = await OpenAsync(ct);
        return await CountUnreadAsync(connection, recipient, projectId, ct);
    }

    private static async Task<int> CountUnreadAsync(MySqlConnection connection, string recipient, Guid projectId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_message_deliveries WHERE recipient = @recipient AND project_id = @projectId AND read_at IS NULL",
            new { recipient, projectId }, cancellationToken: ct));

    // ---- ack -------------------------------------------------------------------------------------

    public Task<AckResult> AckAsync(AckSpec spec, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            DreamPolicy.Refuse(actor, "AckAsync");
            var now = Now();
            // Closing a blocking question bumps its item, so that item's row comes first.
            var holds = new Dictionary<Guid, bool>();
            if (spec is { Resolution: not null, Messages: { } named })
            {
                foreach (var messageId in named.Distinct())
                {
                    var namedMessage = await ReadMessageRowAsync(connection, null, messageId, ct);
                    if (namedMessage is { Blocking: true, ItemId: { } blockedItem })
                        holds[blockedItem] = true;
                    // Its answer may fall back to the item's holder: a claim either sees it or follows it.
                    else if (namedMessage is { SenderSessionId: not null, ItemId: { } askedItem })
                        holds.TryAdd(askedItem, false);
                }
            }

            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var messageLock = new MessageLockScope(connection, transaction);
            await HoldItemsAsync(connection, transaction, holds, ct);
            await messageLock.AcquireAsync(ct);
            now = Now();
            var acked = new List<Guid>();
            var entries = new List<PendingChange>();
            var versions = new List<ItemVersionRef>();

            if (spec.Messages is { } ids)
            {
                foreach (var messageId in ids.Distinct())
                {
                    var message = await ReadMessageRowAsync(connection, transaction, messageId, ct);
                    var delivery = message is null || message.ProjectId != spec.ProjectId
                        ? null
                        : await connection.QuerySingleOrDefaultAsync<DeliveryRow>(new CommandDefinition(
                            "SELECT state, read_at FROM work_message_deliveries WHERE message_id = @messageId AND recipient = @recipient",
                            new { messageId, recipient = spec.Recipient }, transaction, cancellationToken: ct));
                    if (message is null || delivery is null)
                        throw WorkException.NotFound($"Message {messageId} is not in this inbox. Nothing was changed.");

                    if (delivery.ReadAt is null)
                    {
                        await MarkReadAsync(connection, transaction, messageId, spec.Recipient, now, ct);
                        await InsertMessageEventAsync(connection, transaction, spec.ProjectId, messageId, MessageEventKinds.Read, spec.Recipient, actor, now, ct);
                        acked.Add(messageId);
                    }

                    if (spec.Resolution is { } resolution)
                    {
                        // Closing it again the same way, by the same recipient, changes nothing (a retried ack).
                        var again = message.State == JsonName(resolution) && message.ResolvedBySessionId == actor.SessionId && message.ResolvedBy == actor.Name;
                        if (!again)
                        {
                            await ResolveAsync(connection, transaction, message, resolution, actor, now, ct);
                            if (message.Blocking && message.ItemId is { } unblocked)
                                Collect(await AwaitingHumanChangeAsync(connection, transaction, unblocked, opened: false, now, ct), entries, versions);
                            if (!acked.Contains(messageId))
                                acked.Add(messageId);
                        }
                    }
                }
            }
            else
            {
                var due = (await connection.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT message_id FROM work_message_deliveries WHERE recipient = @recipient AND project_id = @projectId AND sequence <= @upTo AND read_at IS NULL ORDER BY sequence",
                    new { recipient = spec.Recipient, projectId = spec.ProjectId, upTo = spec.UpTo }, transaction, cancellationToken: ct))).ToList();
                foreach (var messageId in due)
                {
                    await MarkReadAsync(connection, transaction, messageId, spec.Recipient, now, ct);
                    await InsertMessageEventAsync(connection, transaction, spec.ProjectId, messageId, MessageEventKinds.Read, spec.Recipient, actor, now, ct);
                    acked.Add(messageId);
                }
            }

            var lines = new List<MessageLine>();
            foreach (var messageId in acked.Take(50))
            {
                var row = await connection.QuerySingleAsync<MessageRow>(new CommandDefinition(
                    $"SELECT {MessageColumns}{DeliveryColumns} FROM work_message_deliveries d JOIN work_messages m ON m.id = d.message_id{MessageJoins} " +
                    "WHERE d.message_id = @messageId AND d.recipient = @recipient",
                    new { messageId, recipient = spec.Recipient }, transaction, cancellationToken: ct));
                lines.Add(ToLine(row));
            }

            await CommitAsync(connection, transaction, entries, actor, now, ct);
            return new AckResult(acked.Count, lines, versions.Count == 0 ? null : versions);
        }, ct);

    private sealed class DeliveryRow
    {
        public string State { get; init; } = "";
        public DateTime? ReadAt { get; init; }
    }

    private static Task MarkReadAsync(MySqlConnection connection, MySqlTransaction transaction, Guid messageId, string recipient, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_message_deliveries SET state = CASE WHEN state IN ('answered', 'declined') THEN state ELSE 'read' END, " +
            "delivered_at = COALESCE(delivered_at, @now), read_at = COALESCE(read_at, @now) WHERE message_id = @messageId AND recipient = @recipient",
            new { messageId, recipient, now }, transaction, cancellationToken: ct));

    // ---- Handover and inheritance ----------------------------------------------------------------

    /// <summary>
    /// A session has claimed an item (a recovery, a release taken over, a pivot): the item's open
    /// questions and requests, not its own and not to the human, go to its inbox (§5.9). Takes the
    /// messages lock, which the caller's <paramref name="scope"/> releases after the commit; its
    /// locking read sees every post committed before. The message stays open; the delivery is new, so
    /// a replay from any cursor reaches it.
    /// </summary>
    private async Task HandOverOpenMessagesAsync(
        MySqlConnection connection, MySqlTransaction transaction, MessageLockScope scope, Guid itemId, WorkActor actor, DateTime now, CancellationToken ct)
    {
        if (actor.SessionId is not { } session)
            return;

        // An unlocked look first, on a second connection (current data, whatever this transaction has
        // read): a post to the item's thread holds the item's row shared, so every post that
        // committed before this claim's row lock is seen here, and one that follows sees the new
        // holder. Only when something is open is the lock taken and the look repeated under it.
        var staleBefore = StaleBefore(now);
        await using (var look = await OpenAsync(ct))
        {
            if (await look.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM work_messages m WHERE m.item_id = @itemId AND m.state = 'open' AND m.kind IN ('question', 'request') " +
                    "AND m.to_human = 0 AND (m.sender_session_id IS NULL OR m.sender_session_id <> @session)) " +
                    $"OR EXISTS (SELECT 1 {UnreadAnswersOfGoneAskers})",
                    new { itemId, session, staleBefore }, cancellationToken: ct)) == 0)
                return;
        }

        await scope.AcquireAsync(ct);
        var open = (await connection.QueryAsync<(Guid Id, Guid ProjectId)>(new CommandDefinition(
            "SELECT m.id, m.project_id FROM work_messages m WHERE m.item_id = @itemId AND m.state = 'open' AND m.kind IN ('question', 'request') " +
            "AND m.to_human = 0 AND (m.sender_session_id IS NULL OR m.sender_session_id <> @session) " +
            "AND NOT EXISTS (SELECT 1 FROM work_message_deliveries d WHERE d.message_id = m.id AND d.recipient = @session) ORDER BY m.sequence FOR UPDATE",
            new { itemId, session }, transaction, cancellationToken: ct))).ToList();
        // The answers to the item's questions whose asker ended or fell silent before reading them (CG-180).
        // Read on a connection of its own under the messages lock, so it is current without locking the
        // session and delivery rows it joins (code holding the lock takes no further row locks).
        await using (var answers = await OpenAsync(ct))
            open.AddRange(await answers.QueryAsync<(Guid Id, Guid ProjectId)>(new CommandDefinition(
                $"SELECT COALESCE(q.resolution_message_id, q.id), q.project_id {UnreadAnswersOfGoneAskers} ORDER BY q.resolved_at, q.sequence",
                new { itemId, session, staleBefore }, cancellationToken: ct)));
        foreach (var (id, projectId) in open)
        {
            if (await InsertDeliveryAsync(connection, transaction, id, projectId, session, DeliveryState.Pending, handedOver: true, now, ct))
                await InsertMessageEventAsync(connection, transaction, projectId, id, MessageEventKinds.HandedOver, session, actor, now, ct);
        }
    }

    // The answers to an item's closed questions (the reply that closed one, or the question itself when
    // ack closed it) that no session has read: the asking session (not @session) ended, fell silent
    // before @staleBefore or was pruned, no session read it as an answer, and @session does not have it yet.
    // Once a session reads it, it passes on no further.
    private const string UnreadAnswersOfGoneAskers =
        "FROM work_messages q " +
        "JOIN work_message_deliveries ad ON ad.message_id = COALESCE(q.resolution_message_id, q.id) AND ad.recipient = q.sender_session_id " +
        "LEFT JOIN work_sessions asker ON asker.id = q.sender_session_id " +
        "WHERE q.item_id = @itemId AND q.state IN ('answered', 'declined') AND q.sender_session_id <> @session AND ad.read_at IS NULL " +
        "AND (asker.id IS NULL OR asker.ended_at IS NOT NULL OR asker.last_seen_at <= @staleBefore) " +
        // Read by a session it was delivered to as an answer: any reader of the reply; for a question
        // closed by ack, a delivery made at or after its closing to a session other than the resolver
        // (the resolver's own copy, and reads of the open question, are not reads of the answer).
        "AND NOT EXISTS (SELECT 1 FROM work_message_deliveries x WHERE x.message_id = ad.message_id AND x.recipient <> 'human' AND x.read_at IS NOT NULL " +
        "AND (q.resolution_message_id IS NOT NULL OR (x.created_at >= q.resolved_at AND x.recipient <> COALESCE(q.resolved_by_session_id, '')))) " +
        "AND NOT EXISTS (SELECT 1 FROM work_message_deliveries own WHERE own.message_id = ad.message_id AND own.recipient = @session)";

    /// <summary>
    /// The unread (or still open) direct messages with no item that a new session of a project and
    /// repository inherits (§5.9): deliveries to a session that ended, or fell silent past the idle
    /// threshold, whose message was sent to a session of the same repository that ended within the
    /// last seven days (the window runs from that original session's end, so a message passed on to a
    /// session that then crashed can pass on again). Each source delivery passes once (handed_to);
    /// a new session never gets one it holds already. Read on a connection of its own, so what it
    /// sees is current whatever the calling transaction has read.
    /// </summary>
    private async Task<List<(long Sequence, Guid MessageId)>> InheritableDeliveriesAsync(Guid projectId, string session, string? repository, DateTime now, CancellationToken ct)
    {
        await using var look = await OpenAsync(ct);
        return (await look.QueryAsync<(long Sequence, Guid MessageId)>(new CommandDefinition(
            "SELECT d.sequence, d.message_id FROM work_message_deliveries d JOIN work_messages m ON m.id = d.message_id " +
            "JOIN work_sessions holder ON holder.id = d.recipient JOIN work_sessions original ON original.id = m.to_session_id " +
            "WHERE d.project_id = @projectId AND d.handed_to IS NULL AND d.recipient <> @session AND m.item_id IS NULL AND m.to_session_id IS NOT NULL " +
            "AND (holder.ended_at IS NOT NULL OR holder.last_seen_at <= @staleBefore) " +
            "AND COALESCE(original.ended_at, original.last_seen_at) > @retentionStart AND original.repository_name <=> @repository " +
            "AND (d.read_at IS NULL OR m.state = 'open') " +
            "AND NOT EXISTS (SELECT 1 FROM work_message_deliveries own WHERE own.message_id = d.message_id AND own.recipient = @session) ORDER BY d.sequence",
            new { projectId, session, staleBefore = StaleBefore(now), retentionStart = now - WorkMessageLimits.DirectMessageRetention, repository },
            cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// A new session inherits what <see cref="InheritableDeliveriesAsync"/> finds, exactly once. An
    /// unlocked look decides whether the messages lock is needed at all; under it the look is repeated
    /// and only the delivery rows are locked. The caller holds the project's row.
    /// </summary>
    private async Task InheritDirectMessagesAsync(
        MySqlConnection connection, MySqlTransaction transaction, MessageLockScope scope, Guid projectId, string? repository, WorkActor actor, DateTime now, CancellationToken ct)
    {
        var session = actor.SessionId!;
        if ((await InheritableDeliveriesAsync(projectId, session, repository, now, ct)).Count == 0)
            return;

        await scope.AcquireAsync(ct);
        var candidates = (await InheritableDeliveriesAsync(projectId, session, repository, now, ct)).Select(entry => entry.Sequence).ToArray();
        if (candidates.Length == 0)
            return;

        // Only the delivery rows are locked; sessions are read without locks.
        var inherited = (await connection.QueryAsync<(long Sequence, Guid MessageId)>(new CommandDefinition(
            "SELECT sequence, message_id FROM work_message_deliveries WHERE sequence IN @candidates AND handed_to IS NULL ORDER BY sequence FOR UPDATE",
            new { candidates }, transaction, cancellationToken: ct))).ToList();
        foreach (var (sequence, messageId) in inherited)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_message_deliveries SET handed_to = @session WHERE sequence = @sequence", new { session, sequence }, transaction, cancellationToken: ct));
            await InsertDeliveryAsync(connection, transaction, messageId, projectId, session, DeliveryState.Pending, handedOver: true, now, ct);
            await InsertMessageEventAsync(connection, transaction, projectId, messageId, MessageEventKinds.HandedOver, session, actor, now, ct);
        }
    }

    // ---- Reads for views -------------------------------------------------------------------------

    /// <summary>The open questions and requests of an item's thread, oldest first, as lines; null when there are none.</summary>
    private static async Task<IReadOnlyList<MessageLine>?> ReadOpenMessagesAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            $"SELECT {MessageColumns} FROM work_messages m{MessageJoins} WHERE m.item_id = @itemId AND m.state = 'open' ORDER BY m.sequence LIMIT {WorkMessageLimits.MaxOpenMessages}",
            new { itemId }, transaction, cancellationToken: ct));
        var lines = rows.Select(ToLine).ToList();
        return lines.Count == 0 ? null : lines;
    }

    /// <summary>The item's brief with its thread: open questions and requests, and its questions with their answers (next_work, every path).</summary>
    private static async Task<ItemView?> WithThreadAsync(MySqlConnection connection, ItemView? view, CancellationToken ct) =>
        view is null ? null : view with
        {
            OpenMessages = await ReadOpenMessagesAsync(connection, null, view.Id, ct),
            Questions = await ReadItemQuestionsAsync(connection, null, view.Id, ct)
        };

    /// <summary>The newest questions and requests of an item's thread, open and closed, oldest first, each with its answer; null when there are none.</summary>
    private static async Task<IReadOnlyList<MessageView>?> ReadItemQuestionsAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<MessageRow>(new CommandDefinition(
            $"SELECT {MessageColumns} FROM work_messages m{MessageJoins} WHERE m.item_id = @itemId AND m.kind IN ('question', 'request') " +
            $"ORDER BY m.sequence DESC LIMIT {WorkMessageLimits.MaxItemQuestions}",
            new { itemId }, transaction, cancellationToken: ct))).ToList();
        rows.Reverse();
        return rows.Count == 0 ? null : rows.Select(ToView).ToList();
    }

    private static async Task<bool> HasOpenBlockingQuestionAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_messages m JOIN work_items i ON i.id = m.item_id WHERE m.item_id = @itemId AND m.blocking = 1 AND m.state = 'open' " +
            "AND i.status NOT IN ('complete', 'closed')",
            new { itemId }, transaction, cancellationToken: ct)) > 0;

    /// <summary>The briefing's inbox: the unacknowledged count and the oldest few, as lines; null when nothing waits.</summary>
    private static async Task<BriefingInbox?> ReadBriefingInboxAsync(MySqlConnection connection, Guid projectId, string session, CancellationToken ct)
    {
        var unread = await CountUnreadAsync(connection, session, projectId, ct);
        if (unread == 0)
            return null;

        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            $"SELECT {MessageColumns}{DeliveryColumns} FROM work_message_deliveries d JOIN work_messages m ON m.id = d.message_id{MessageJoins} " +
            $"WHERE d.recipient = @session AND d.project_id = @projectId AND d.read_at IS NULL ORDER BY d.sequence LIMIT {WorkMessageLimits.MaxBriefingInboxLines}",
            new { session, projectId }, cancellationToken: ct));
        return new BriefingInbox(unread, rows.Select(ToLine).ToList());
    }

    // ---- Events ----------------------------------------------------------------------------------

    private sealed class MessageEventRow
    {
        public long Sequence { get; init; }
        public string ProjectKey { get; init; } = "";
        public Guid MessageId { get; init; }
        public string Kind { get; init; } = "";
        public string? Recipient { get; init; }
        public string ActorName { get; init; } = "";
        public string ActorKind { get; init; } = "";
        public string? ActorSessionId { get; init; }
        public string? Harness { get; init; }
        public string? Model { get; init; }
        public string? Host { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    public async Task<IReadOnlyList<MessageEventView>> ReadMessageEventsAsync(Guid projectId, long afterSequence, int limit, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var connection = await OpenAsync(ct);
        var events = (await connection.QueryAsync<MessageEventRow>(new CommandDefinition(
            "SELECT e.sequence, p.project_key, e.message_id, e.kind, e.recipient, e.actor_name, e.actor_kind, e.actor_session_id, COALESCE(s.harness, e.actor_harness) AS harness, COALESCE(s.model, e.actor_model) AS model, COALESCE(s.host, e.actor_host) AS host, e.created_at " +
            "FROM work_message_events e JOIN work_projects p ON p.id = e.project_id LEFT JOIN work_sessions s ON s.id = e.actor_session_id " +
            "WHERE e.project_id = @projectId AND e.sequence > @afterSequence ORDER BY e.sequence LIMIT @limit",
            new { projectId, afterSequence, limit }, cancellationToken: ct))).ToList();
        if (events.Count == 0)
            return [];

        var ids = events.Select(entry => entry.MessageId).Distinct().ToArray();
        var lines = (await connection.QueryAsync<MessageRow>(new CommandDefinition(
            $"SELECT {MessageColumns} FROM work_messages m{MessageJoins} WHERE m.id IN @ids", new { ids }, cancellationToken: ct)))
            .ToDictionary(row => row.Id, ToLine);
        return events.Select(entry => new MessageEventView(
            entry.Sequence, entry.ProjectKey, entry.MessageId, entry.Kind,
            new WorkActor(entry.ActorName, entry.ActorKind, entry.ActorSessionId, entry.Harness, entry.Model, entry.Host),
            Utc(entry.CreatedAt), entry.Recipient, lines.GetValueOrDefault(entry.MessageId))).ToList();
    }

    public async Task<MessageListPage> ListMessagesAsync(MessageListQuery query, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(query.ProjectId);
        await using var connection = await OpenAsync(ct);
        var limit = Math.Clamp(query.Limit, 1, WorkLimits.MaxPageSize);
        var rows = (await connection.QueryAsync<MessageRow>(new CommandDefinition(
            $"SELECT {MessageColumns} FROM work_messages m{MessageJoins} WHERE m.project_id = @projectId " +
            "AND (@channel IS NULL OR m.channel = @channel) AND (@itemId IS NULL OR m.item_id = @itemId) " +
            "AND (@featureId IS NULL OR m.feature_id = @featureId) AND (@resource IS NULL OR m.resource_name = @resource) " +
            "AND (@openOnly = 0 OR m.state = 'open') AND (@toHuman = 0 OR m.to_human = 1) " +
            (query.Resolved
                // Most recently closed first, so answering an old question puts it at the head.
                ? "AND m.state IN ('answered', 'declined') " +
                  "AND (@beforeResolvedAt IS NULL OR m.resolved_at < @beforeResolvedAt OR (m.resolved_at = @beforeResolvedAt AND m.sequence < @before)) " +
                  "ORDER BY m.resolved_at DESC, m.sequence DESC LIMIT @take"
                : "AND (@before IS NULL OR m.sequence < @before) ORDER BY m.sequence DESC LIMIT @take"),
            new
            {
                projectId = query.ProjectId, channel = query.Channel, itemId = query.ItemId, featureId = query.FeatureId, resource = query.Resource,
                openOnly = query.OpenOnly ? 1 : 0, beforeResolvedAt = query.BeforeResolvedAt, toHuman = query.ToHuman ? 1 : 0, before = query.Before, take = limit + 1
            }, cancellationToken: ct))).ToList();
        var page = rows.Take(limit).ToList();
        var next = rows.Count <= limit ? null
            : query.Resolved ? ResolvedCursor.Format(page[^1].ResolvedAt!.Value, page[^1].Sequence)
            : page[^1].Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new MessageListPage(page.Select(ToView).ToList(), next);
    }

    public async Task<long> GetMessageEventHeadAsync(CancellationToken ct = default)
    {
        DreamPolicy.RefuseAmbient("read the global message head");
        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT MAX(sequence) FROM work_message_events", cancellationToken: ct)) ?? 0;
    }
}
