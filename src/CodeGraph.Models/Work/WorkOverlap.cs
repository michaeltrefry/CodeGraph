namespace CodeGraph.Models.Work;

// Work-area overlap warnings (sc-27872 phase 2; design §5.6). Computed on read, never stored: two
// live sessions whose claimed items' `touches` overlap, directly or through the graph's impact
// radius, are each told of the other, by session id, so they can message each other. Advisory
// only (E6): nothing is blocked.

/// <summary>The area one live session is working in: a claimed item and what it touches.</summary>
public sealed record WorkArea(
    string SessionId,
    string Identity,
    string Harness,
    string ItemKey,
    string? Repository,
    IReadOnlyList<string> Touches,
    string? Model = null,
    string? Host = null);

public static class OverlapKinds
{
    /// <summary>The same path, a path inside the other's directory or glob, or the same symbol.</summary>
    public const string Direct = "direct";

    /// <summary>One item touches something inside the other's impact radius (analyze_impact).</summary>
    public const string Impact = "impact";
}

/// <summary>A warning to the session that receives it, naming the other session and its item.</summary>
public sealed record OverlapWarning(
    string Kind,
    string YourItem,
    string OtherItem,
    string OtherSessionId,
    string OtherIdentity,
    string OtherHarness,
    string Via,
    string Message);

public static class WorkOverlapLimits
{
    /// <summary>Impact depth followed from a touched file or symbol.</summary>
    public const int ImpactDepth = 2;

    /// <summary>Touches of one item resolved through the graph.</summary>
    public const int MaxTouchesResolved = 10;

    /// <summary>Warnings in one answer.</summary>
    public const int MaxWarnings = 10;

    /// <summary>Wall-clock budget for the impact lookups of one call; past it only direct overlaps are reported.</summary>
    public const int ImpactBudgetMilliseconds = 3_000;
}
