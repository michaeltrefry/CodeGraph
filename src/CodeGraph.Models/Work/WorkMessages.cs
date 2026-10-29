using System.ComponentModel;

namespace CodeGraph.Models.Work;

// Durable messages (sc-27871, design §5.5 and §5.9 "Messages outlive sessions"). A message is data:
// who said what to whom about what. CodeGraph stores it, delivers it and keeps its state per
// recipient; it enforces no norms and an agent never assigns work to another through it (E6).

public enum MessageKind
{
    Info,
    Question,
    Request,
    Blocker,
    Announcement
}

/// <summary>The state of a question or a request; any other kind has none.</summary>
public enum MessageState
{
    Open,
    Answered,
    Declined
}

/// <summary>
/// The state of one recipient's copy: pending (stored, not yet returned), delivered (returned by
/// inbox or a briefing), read (acknowledged by ack), answered or declined (the recipient resolved it).
/// </summary>
public enum DeliveryState
{
    Pending,
    Delivered,
    Read,
    Answered,
    Declined
}

/// <summary>How a question or request is closed.</summary>
public enum MessageResolution
{
    Answered,
    Declined
}

public static class MessageChannels
{
    public const string Item = "item";
    public const string Project = "project";
    public const string Feature = "feature";
    public const string Resource = "resource";
    public const string Session = "session";
    public const string Human = "human";

    /// <summary>The recipient value of the project's humans in a delivery.</summary>
    public const string HumanRecipient = "human";
}

public static class MessageEventKinds
{
    public const string Posted = "posted";
    public const string HandedOver = "handed_over";
    public const string Read = "read";
    public const string Answered = "answered";
    public const string Declined = "declined";
}

public static class WorkMessageLimits
{
    public const int MaxBodyLength = 8_000;
    public const int MaxResourceLength = 200;
    public const int DefaultInboxLimit = 10;
    public const int MaxInboxLimit = 100;
    public const int MaxAckMessages = 100;
    /// <summary>The characters of a one-line summary of a message body.</summary>
    public const int SummaryLength = 160;
    public const int MaxBriefingInboxLines = 10;
    public const int MaxOpenMessages = 20;
    /// <summary>A direct message to a session with no item is passed to the next session of its project and repository for this long after the session ends.</summary>
    public static readonly TimeSpan DirectMessageRetention = TimeSpan.FromDays(7);
}

public sealed class PostRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("The message text, 1 to 8000 characters.")]
    public required string Body { get; init; }

    [Description("info (default), question, request, blocker or announcement. A question and a request stay open until answered or declined.")]
    public MessageKind? Kind { get; init; }

    [Description("Key or id of a Project. May be left out when item or session_id names it.")]
    public string? Project { get; init; }

    [Description("Key or id of the item the message is about. Without another target the message goes to the item's thread (delivered to the session holding the item now, and handed to the next holder while it is open). Combines with to_session and to_human.")]
    public string? Item { get; init; }

    [Description("Key or id of a feature: the channel of the sessions holding items of that feature. Not with item.")]
    public string? Feature { get; init; }

    [Description("Name of a resource (such as gpu:4090-box): the resource channel, delivered to the project's live sessions. Not with item.")]
    public string? Resource { get; init; }

    [Description("Id of a session of the project: a direct message. With item it is stored on the item's thread as well.")]
    public string? ToSession { get; init; }

    [Description("true: to the project's humans (their inbox and the UI). With item it is on the item's thread.")]
    public bool? ToHuman { get; init; }

    [Description("true on a question to a human about an item: the item is Blocked (blocked_reason awaiting_human) until the question is answered or declined. A last resort for what only a person can give (credentials, money, irreversible actions).")]
    public bool? Blocking { get; init; }

    [Description("Id of the message this one replies to. Without another target the reply goes to that message's sender, about the same item.")]
    public string? InReplyTo { get; init; }

    [Description("answered or declined: closes the question or request named in in_reply_to, as the caller.")]
    public MessageResolution? Resolves { get; init; }

    [Description("Caller-chosen key: a repeat by the same identity in this project answers the first message, with created false.")]
    public string? IdempotencyKey { get; init; }
}

public sealed class InboxRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId + " Without it the caller reads the project's humans' inbox (project is then required).")]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project. Required without session_id; with it, it must be the session's project.")]
    public string? Project { get; init; }

    [Description("next_cursor of an earlier call. Without it the answer replays every message not yet acknowledged with ack, so a session that crashed after delivery but before ack receives them again; with it, only those after the cursor.")]
    public string? Cursor { get; init; }

    [Description("true: also messages already acknowledged.")]
    public bool? IncludeRead { get; init; }

    [Description("Most messages to return, 1 to 100 (default 10).")]
    public int? Limit { get; init; }

    [Description("Wait up to this many seconds (at most 55) for a message before answering with none.")]
    public int? WaitSeconds { get; init; }
}

public sealed class AckRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId + " Without it the caller acts for the project's humans (project is then required).")]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project. Required without session_id.")]
    public string? Project { get; init; }

    [Description("Ids of messages in the caller's inbox to mark read. Exactly one of messages and up_to.")]
    public List<string>? Messages { get; init; }

    [Description("A next_cursor of inbox: marks read every message delivered up to it. Exactly one of messages and up_to.")]
    public string? UpTo { get; init; }

    [Description("answered or declined: also closes the named questions or requests as the caller (with messages only).")]
    public MessageResolution? Resolution { get; init; }
}

/// <summary>A message in full.</summary>
public sealed record MessageView(
    Guid Id,
    string ProjectKey,
    string Channel,
    MessageKind Kind,
    WorkActor From,
    string Body,
    DateTime CreatedAt,
    MessageState? State = null,
    bool? Blocking = null,
    string? ToSessionId = null,
    bool? ToHuman = null,
    string? Item = null,
    string? Feature = null,
    string? Resource = null,
    Guid? InReplyTo = null,
    WorkActor? ResolvedBy = null,
    DateTime? ResolvedAt = null,
    DeliveryState? Delivery = null,
    bool? HandedOver = null);

/// <summary>One line of a list (E4): a message to choose from, not to read.</summary>
public sealed record MessageLine(
    Guid Id,
    MessageKind Kind,
    string From,
    string Summary,
    DateTime CreatedAt,
    string? FromSessionId = null,
    string? Item = null,
    MessageState? State = null,
    bool? Blocking = null,
    DeliveryState? Delivery = null,
    bool? HandedOver = null);

public sealed record PostResult(bool Created, MessageView Message, int Recipients, MessageView? Resolved = null);

public sealed record InboxResult(IReadOnlyList<MessageView> Items, string NextCursor);

public sealed record AckResult(int Acked, IReadOnlyList<MessageLine> Messages);

/// <summary>The briefing's inbox: how many messages wait unacknowledged, and the first of them.</summary>
public sealed record BriefingInbox(int Unread, IReadOnlyList<MessageLine>? Items = null);

/// <summary>One entry of the stream the UI follows.</summary>
public sealed record MessageEventView(
    long Sequence,
    string ProjectKey,
    Guid MessageId,
    string Kind,
    WorkActor Actor,
    DateTime CreatedAt,
    string? Recipient = null,
    MessageLine? Message = null);
