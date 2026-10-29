using CodeGraph.Models.Work;

namespace CodeGraph.Data;

/// <summary>
/// Persistence of messages (sc-27871): posts, per-recipient deliveries, inbox replay from a cursor,
/// acknowledgements, answers, the unread count and the event stream of the UI. Part of
/// <see cref="IWorkStore"/>; refused calls throw <see cref="WorkException"/> and change nothing.
/// </summary>
public interface IWorkMessageStore
{
    Task<PostedMessage> PostMessageAsync(NewMessageSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default);

    /// <summary>
    /// The deliveries to <see cref="InboxQuery.Recipient"/> after the cursor, oldest first, each
    /// returned one now at least delivered. Unacknowledged ones only unless asked for all.
    /// </summary>
    Task<InboxPage> ReadInboxAsync(InboxQuery query, CancellationToken ct = default);

    /// <summary>The newest delivery sequence of the store, 0 when none; a cursor beyond it is not one of this database.</summary>
    Task<long> GetInboxHeadAsync(CancellationToken ct = default);

    Task<AckResult> AckAsync(AckSpec spec, WorkActor actor, CancellationToken ct = default);

    /// <summary>Deliveries to the recipient that are not acknowledged yet (an index range, cheap on every call).</summary>
    Task<int> CountUnreadAsync(string recipient, Guid projectId, CancellationToken ct = default);

    /// <summary>Marks the recipient's pending deliveries of these messages delivered (they were shown in a briefing).</summary>
    Task MarkDeliveredAsync(string recipient, IReadOnlyList<Guid> messageIds, CancellationToken ct = default);

    Task<IReadOnlyList<MessageEventView>> ReadMessageEventsAsync(Guid projectId, long afterSequence, int limit, CancellationToken ct = default);

    /// <summary>The newest message event sequence, 0 when none.</summary>
    Task<long> GetMessageEventHeadAsync(CancellationToken ct = default);

    /// <summary>
    /// A read-only listing of a project's messages for the UI (sc-27874), newest first, whatever the
    /// channel and whoever they were delivered to. Changes nothing, not even a delivery state.
    /// </summary>
    Task<MessageListPage> ListMessagesAsync(MessageListQuery query, CancellationToken ct = default);
}

/// <param name="ItemId">The item whose thread this is, or null.</param>
/// <param name="ToSession">A direct message to this session of the project.</param>
public sealed record NewMessageSpec(
    Guid ProjectId,
    string Body,
    MessageKind Kind,
    Guid? ItemId,
    Guid? FeatureId,
    string? Resource,
    Guid? ToSession,
    bool ToHuman,
    bool Blocking,
    Guid? InReplyTo,
    MessageResolution? Resolves);

public sealed record PostedMessage(bool Created, MessageView Message, int Recipients, MessageView? Resolved, IReadOnlyList<ItemVersionRef>? ItemVersions = null);

/// <param name="Recipient">A session id, or <see cref="MessageChannels.HumanRecipient"/>.</param>
public sealed record InboxQuery(Guid ProjectId, string Recipient, long AfterSequence, bool IncludeRead, int Limit);

/// <param name="LastSequence">The delivery sequence of the last item, or the query's cursor when there are none.</param>
public sealed record InboxPage(IReadOnlyList<MessageView> Items, long LastSequence);

/// <param name="Messages">Messages to mark read, or null; <paramref name="UpTo"/> then names a delivery sequence.</param>
public sealed record AckSpec(Guid ProjectId, string Recipient, IReadOnlyList<Guid>? Messages, long? UpTo, MessageResolution? Resolution);

/// <param name="Channel">Only this channel (<see cref="MessageChannels"/>), or null.</param>
/// <param name="ItemId">Only the thread of this item.</param>
/// <param name="FeatureId">Only messages about this feature.</param>
/// <param name="Resource">Only messages on this resource channel.</param>
/// <param name="OpenOnly">Only questions and requests that are still open.</param>
/// <param name="ToHuman">Only messages addressed to the humans.</param>
/// <param name="Before">Only messages older than this message sequence (a page cursor), or null.</param>
public sealed record MessageListQuery(
    Guid ProjectId,
    string? Channel,
    Guid? ItemId,
    Guid? FeatureId,
    string? Resource,
    bool OpenOnly,
    bool ToHuman,
    long? Before,
    int Limit);

/// <param name="NextBefore">The cursor of the next (older) page, or null on the last page.</param>
public sealed record MessageListPage(IReadOnlyList<MessageView> Items, string? NextBefore);
