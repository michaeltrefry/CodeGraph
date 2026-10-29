using System.Text.RegularExpressions;

namespace CodeGraph.Models.Work;

// GitHub evidence on work items (agent coordination S6, sc-27870; design §7).

/// <summary>The kind of thing GitHub reported about an item.</summary>
public enum GitHubEvidenceKind
{
    /// <summary>A branch was pushed.</summary>
    Push,
    /// <summary>A pull request is open (opened, reopened, edited, new commits, ready for review).</summary>
    PullRequestOpen,
    /// <summary>A pull request was merged.</summary>
    PullRequestMerged,
    /// <summary>A pull request was closed without merging.</summary>
    PullRequestClosed,
    /// <summary>A pull request review was submitted.</summary>
    Review,
    /// <summary>A review comment on a pull request diff.</summary>
    ReviewComment,
    /// <summary>A comment on the pull request conversation.</summary>
    IssueComment
}

public static class GitHubEvidenceSources
{
    public const string Human = "human";
    public const string Bot = "bot";
    public const string HighPriority = "high";
    public const string NormalPriority = "normal";
}

/// <summary>The actor of a write driven by a GitHub webhook: the GitHub login, as a system actor.</summary>
public static class GitHubActors
{
    public const string SystemKind = "system";

    public static WorkActor For(string login) => new($"github:{login}", SystemKind);
}

/// <summary>
/// One webhook event, reduced to what the work store needs. <see cref="References"/> are the
/// texts scanned for item keys and aliases (branch name, PR title, PR body).
/// </summary>
/// <param name="DeliveryId">The delivery's id: with the item, the idempotency key of the write.</param>
/// <param name="Repositories">Names the repository goes by (indexed name, short name, full name); an item matches when its project holds one.</param>
/// <param name="Branch">The branch of a push, or the head branch of a PR (only used to tie the event to an attempt).</param>
/// <param name="BranchUrl">The URL a pushed branch is linked under (push only).</param>
/// <param name="PullRequestUrl">The PR's URL; an item that links it is a target whatever the text says.</param>
/// <param name="Description">The PR description, kept whole on a close or merge.</param>
/// <param name="ExternalId">The id of the review or comment (the evidence key of a review).</param>
public sealed record GitHubEvidenceSpec(
    string DeliveryId,
    GitHubEvidenceKind Kind,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<string> References,
    string ActorLogin,
    string? Branch = null,
    string? BranchUrl = null,
    string? PullRequestUrl = null,
    int? PullRequestNumber = null,
    string? Title = null,
    string? Description = null,
    string? Url = null,
    string? ExternalId = null,
    string? ReviewState = null,
    string? Location = null,
    string? Body = null,
    string Source = GitHubEvidenceSources.Human,
    string Priority = GitHubEvidenceSources.NormalPriority);

/// <param name="Result"><c>applied</c>, <c>duplicate</c> (this delivery was handled already) or <c>unchanged</c>.</param>
/// <param name="Superseded">The event belongs to a superseded attempt: recorded, nothing moved.</param>
public sealed record GitHubEvidenceItemResult(
    Guid ItemId,
    string Key,
    string Result,
    WorkStatus? From,
    WorkStatus? To,
    bool Superseded,
    bool FlaggedMerged = false);

public sealed record GitHubEvidenceResult(IReadOnlyList<GitHubEvidenceItemResult> Items)
{
    public static readonly GitHubEvidenceResult None = new([]);
}

/// <summary>A stored piece of GitHub evidence on an item (S9 reads review evidence from here).</summary>
public sealed record GitHubEvidenceView(
    Guid Id,
    string Kind,
    string? Repository,
    int? PullRequest,
    string? Url,
    string Author,
    string Source,
    string Priority,
    string? ReviewState,
    string? Location,
    string? Body,
    Guid? AttemptId,
    bool Superseded,
    DateTime CreatedAt,
    string? ExternalId = null);

public static partial class WorkReferences
{
    // A key or alias is letters and digits, a hyphen, digits; not part of a longer word or number,
    // so SW-1 is not found in SW-12 or XSW-1. Underscore, slash, dot and hyphen separate.
    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Za-z][A-Za-z0-9]{1,9})-([0-9]{1,10})(?![A-Za-z0-9])")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^([a-z][a-z0-9]{1,9})-([0-9]{1,10})$")]
    private static partial Regex AliasPattern();

    /// <summary>
    /// The stored form of an alias: lower case, no leading zeros in the number (<c>SC-0012</c> is
    /// <c>sc-12</c>); null when the text is not an alias or its number is not above zero.
    /// </summary>
    public static string? NormalizeAlias(string? alias)
    {
        var match = AliasPattern().Match(alias?.Trim().ToLowerInvariant() ?? "");
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var number) || number <= 0)
            return null;

        return $"{match.Groups[1].Value}-{number}";
    }

    /// <summary>
    /// The candidate references (<c>SW-142</c>, <c>sc-27866</c>) in a text, lower-cased and distinct.
    /// Whether one names an item is for the store to say.
    /// </summary>
    public static IReadOnlyList<(string Prefix, int Number)> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        return ReferencePattern().Matches(text)
            .Select(match => (Prefix: match.Groups[1].Value.ToLowerInvariant(), Number: int.TryParse(match.Groups[2].Value, out var number) ? number : 0))
            .Where(reference => reference.Number > 0)
            .Distinct()
            .ToList();
    }
}
