using System.Text.RegularExpressions;

namespace CodeGraph.Models.Work;

// The work-tracking vocabulary (agent coordination, epic 27864). Ported from KanbanBoard and
// changed as docs/design/agent-coordination.md §5.2a says: Blocked is derived, not a status;
// Issue became bug; the epic became the feature. On the wire every name is snake_case.

public enum WorkStatus
{
    Backlog,
    Ready,
    InProgress,
    InReview,
    Complete,
    Closed
}

public enum WorkItemType
{
    Story,
    Task,
    Bug
}

public enum WorkPriority
{
    Low,
    Medium,
    High,
    Critical
}

public enum RepositoryRole
{
    Owned,
    Dependency
}

public enum WorkLinkKind
{
    Pr,
    Branch,
    Other
}

public enum WorkEntityKind
{
    Project,
    Feature,
    Item
}

public enum WorkDetail
{
    Summary,
    Full
}

/// <summary>
/// The error vocabulary of every work tool (E1). A refused call changed nothing.
/// </summary>
public static class WorkErrorCodes
{
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string AlreadyClaimed = "already_claimed";
    public const string Cycle = "cycle";
    public const string InvalidArgument = "invalid_argument";
    /// <summary>Other writers kept the call waiting or deadlocked it three times. Retry the same call.</summary>
    public const string Busy = "busy";
    /// <summary>Reserved for S2: a write by a session whose claim lapsed and passed to someone else.</summary>
    public const string ClaimLost = "claim_lost";
}

/// <summary>
/// Work tracking needs the MariaDB storage provider and this deployment runs without it. Not a
/// work error: it is outside the closed vocabulary of <see cref="WorkErrorCodes"/>, so it carries no
/// code and surfaces as a plain server error (MCP isError without structured content, REST 503).
/// </summary>
public sealed class WorkStoreUnavailableException()
    : InvalidOperationException("Work tracking needs the MariaDB storage provider; this deployment runs without it.");

/// <summary>A refused work call. <see cref="Code"/> is one of <see cref="WorkErrorCodes"/>.</summary>
public sealed class WorkException(string code, string message, object? current = null) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>The object as it is now, for a <c>conflict</c> on <c>expected_version</c>.</summary>
    public object? Current { get; } = current;

    public static WorkException Invalid(string message) => new(WorkErrorCodes.InvalidArgument, message);

    public static WorkException NotFound(string message) => new(WorkErrorCodes.NotFound, message);

    public static WorkException Busy(string message) => new(WorkErrorCodes.Busy, message);
}

/// <summary>Limits, ported from KanbanBoard's contract where it has one.</summary>
public static class WorkLimits
{
    public const int MaxPlanItems = 200;
    public const int MaxPlanFeatures = 20;
    public const int MaxPlanDocuments = 20;
    public const int MaxPlanDependencies = 500;
    public const int MaxPlanBytes = 2 * 1024 * 1024;
    public const int MaxRefLength = 64;
    public const int MaxTitleLength = 240;
    public const int MaxProjectNameLength = 160;
    public const int MaxDocumentTitleLength = 200;
    public const int MaxLabelsLength = 4000;
    public const int MaxGoals = 50;
    public const int MaxGoalLength = 1000;
    public const int MaxRepositories = 100;
    public const int MaxLinkUrlLength = 2048;
    public const int MaxIdempotencyKeyLength = 128;
    public const int MaxMarkerLength = 200;
    public const int MaxClaimTtlSeconds = 86_400;
    public const int DefaultClaimTtlSeconds = 3_600;
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;
    public const int MaxWaitSeconds = 55;
    /// <summary>The most characters of one text a change entry stores; the object keeps the whole text.</summary>
    public const int MaxStoredChangeText = 16_384;
}

/// <summary>The kinds of change-feed entry. Kinds are a contract: new ones may be added, none is renamed.</summary>
public static class WorkChangeKinds
{
    public const string ProjectCreated = "project_created";
    public const string ProjectUpdated = "project_updated";
    public const string FeatureCreated = "feature_created";
    public const string FeatureUpdated = "feature_updated";
    public const string ItemCreated = "item_created";
    public const string ItemUpdated = "item_updated";
    public const string Claimed = "claimed";
}

/// <summary>
/// Speakable keys: a project key (<c>SW</c>), item keys <c>SW-142</c>, feature keys <c>SW-F12</c>.
/// Numbers are per project and sequential.
/// </summary>
public static partial class WorkKeys
{
    [GeneratedRegex("^[A-Z][A-Z0-9]{1,9}$")]
    private static partial Regex ProjectKeyPattern();

    [GeneratedRegex("^([A-Za-z][A-Za-z0-9]{1,9})-([Ff]?)([1-9][0-9]{0,9})$")]
    private static partial Regex EntityKeyPattern();

    public const string ProjectKeyRule = "2 to 10 letters and digits, starting with a letter (stored in upper case)";

    public static bool IsValidProjectKey(string key) => ProjectKeyPattern().IsMatch(key);

    public static string Item(string projectKey, int number) => $"{projectKey}-{number}";

    public static string Feature(string projectKey, int number) => $"{projectKey}-F{number}";

    /// <summary>Parses <c>SW-142</c> or <c>SW-F12</c> (any case) into its parts.</summary>
    public static bool TryParse(string value, out string projectKey, out WorkEntityKind kind, out int number)
    {
        projectKey = string.Empty;
        kind = WorkEntityKind.Item;
        number = 0;
        var match = EntityKeyPattern().Match(value.Trim());
        if (!match.Success || !int.TryParse(match.Groups[3].Value, out number))
            return false;

        projectKey = match.Groups[1].Value.ToUpperInvariant();
        kind = match.Groups[2].Length > 0 ? WorkEntityKind.Feature : WorkEntityKind.Item;
        return true;
    }
}

public static class WorkRules
{
    /// <summary>SHA-256, lowercase hex, of an identity and its idempotency key: a key belongs to one identity.</summary>
    public static string HashIdempotencyKey(string identity, string key) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{identity}\n{key}")));

    /// <summary>
    /// Whether <paramref name="item"/> is blocked by <paramref name="candidateBlocker"/>, directly or
    /// through other items, in a graph of indexes: the one rule for a cycle (KanbanBoard's
    /// IsBlockedByAsync). Making the candidate blocked by the item would then close a loop.
    /// </summary>
    public static bool IsBlockedBy(IReadOnlyList<HashSet<int>> blockersOf, int item, int candidateBlocker)
    {
        var seen = new HashSet<int> { item };
        var pending = new Stack<int>([item]);
        while (pending.TryPop(out var current))
        {
            foreach (var blocker in blockersOf[current])
            {
                if (blocker == candidateBlocker)
                    return true;
                if (seen.Add(blocker))
                    pending.Push(blocker);
            }
        }

        return false;
    }
}

public static class WorkStatuses
{
    /// <summary>The snake_case wire name of a status, as stored.</summary>
    public static string Name(WorkStatus status) => status switch
    {
        WorkStatus.Backlog => "backlog",
        WorkStatus.Ready => "ready",
        WorkStatus.InProgress => "in_progress",
        WorkStatus.InReview => "in_review",
        WorkStatus.Complete => "complete",
        WorkStatus.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static WorkStatus Parse(string stored) => stored switch
    {
        "backlog" => WorkStatus.Backlog,
        "ready" => WorkStatus.Ready,
        "in_progress" => WorkStatus.InProgress,
        "in_review" => WorkStatus.InReview,
        "complete" => WorkStatus.Complete,
        "closed" => WorkStatus.Closed,
        _ => throw new InvalidOperationException($"Unknown stored work status '{stored}'.")
    };

    /// <summary>Complete and Closed end the work on an item; its claim is released with the move.</summary>
    public static bool IsFinished(WorkStatus status) => status is WorkStatus.Complete or WorkStatus.Closed;
}
