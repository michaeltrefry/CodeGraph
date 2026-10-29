using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work.Imports;

/// <summary>A story mapped onto an item, before it is placed in a project (design §10.1).</summary>
public sealed record MappedStory(
    long Id,
    string Title,
    WorkItemType Type,
    WorkStatus Status,
    string? Description,
    IReadOnlyList<string> Labels,
    IReadOnlyList<CriterionSpec> AcceptanceCriteria,
    IReadOnlyList<ImportedComment> Comments,
    IReadOnlyList<LinkSpec> Links,
    IReadOnlyList<string> Aliases,
    ImportedView Imported,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<string> RepositoryCandidates,
    IReadOnlyList<long> Blockers,
    IReadOnlyList<(string Verb, long Other)> Relations,
    bool BlockedWithoutEdges,
    IReadOnlyList<string> HistoricalLinks);

/// <summary>
/// The Shortcut → CodeGraph mapping table of design §10.1, as pure functions over parsed payloads.
/// </summary>
public static partial class ShortcutMapper
{
    /// <summary>Changes when the mapping changes, so a re-run rewrites every object once.</summary>
    public const string MappingVersion = "shortcut-1";

    public static WorkStatus Status(ShortcutState? state) =>
        state?.Name.Trim().ToLowerInvariant() switch
        {
            "backlog" => WorkStatus.Backlog,
            "to do" or "todo" or "ready" => WorkStatus.Ready,
            "in progress" => WorkStatus.InProgress,
            "in review" => WorkStatus.InReview,
            "done" or "complete" or "completed" => WorkStatus.Complete,
            // A state outside the Standard workflow maps by its type.
            _ => state?.Type switch
            {
                "unstarted" => WorkStatus.Ready,
                "started" => WorkStatus.InProgress,
                "done" => WorkStatus.Complete,
                _ => WorkStatus.Backlog
            }
        };

    public static WorkItemType Type(string storyType) =>
        storyType.Trim().ToLowerInvariant() switch
        {
            "bug" => WorkItemType.Bug,
            "chore" => WorkItemType.Task,
            _ => WorkItemType.Story
        };

    /// <summary>The default attribution of an account: the Claude and Codex accounts are agents, the rest people.</summary>
    public static ImportMemberMapping DefaultMember(ShortcutMember member)
    {
        var haystack = $"{member.Name} {member.MentionName} {member.Email}".ToLowerInvariant();
        if (haystack.Contains("claude", StringComparison.Ordinal))
            return new ImportMemberMapping(member.Id, "claude", WorkActor.AgentKind);
        if (haystack.Contains("codex", StringComparison.Ordinal))
            return new ImportMemberMapping(member.Id, "codex", WorkActor.AgentKind);
        return new ImportMemberMapping(member.Id, member.MentionName ?? member.Name, WorkActor.UserKind);
    }

    [GeneratedRegex(@"^\s*\[?\s*author\s*:\s*(?<name>[A-Za-z][\w.\-]{0,40})\s*\]?", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorPrefix();

    private static readonly HashSet<string> AgentNames = new(StringComparer.OrdinalIgnoreCase) { "claude", "codex" };

    /// <summary>
    /// Who wrote a comment: an <c>[author: claude]</c> line at the top names the agent that wrote it
    /// (agents often post through the owner's account); otherwise the account's mapping.
    /// </summary>
    public static (string Name, string Kind) Author(string? memberId, string text, IReadOnlyDictionary<string, ImportMemberMapping> members)
    {
        var prefix = AuthorPrefix().Match(text);
        if (prefix.Success)
        {
            var name = prefix.Groups["name"].Value.ToLowerInvariant();
            if (AgentNames.Contains(name))
                return (name, WorkActor.AgentKind);
            if (members.Values.FirstOrDefault(member => member.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } named)
                return (named.Name, named.Kind);
        }

        return memberId is not null && members.TryGetValue(memberId, out var mapped)
            ? (mapped.Name, mapped.Kind)
            : (memberId is null ? "unknown" : $"shortcut:{memberId}", WorkActor.UserKind);
    }

    [GeneratedRegex(@"^https?://(?:www\.)?github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s#?]+)(?<rest>/[^\s]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrl();

    public static WorkLinkKind LinkKind(string url)
    {
        var match = GitHubUrl().Match(url);
        if (!match.Success)
            return WorkLinkKind.Other;
        var rest = match.Groups["rest"].Value;
        return rest.StartsWith("/pull/", StringComparison.OrdinalIgnoreCase) ? WorkLinkKind.Pr
            : rest.StartsWith("/tree/", StringComparison.OrdinalIgnoreCase) ? WorkLinkKind.Branch
            : WorkLinkKind.Other;
    }

    /// <summary>Branches, PRs, commits, external links and linked files as item links (pr / branch / other).</summary>
    public static IReadOnlyList<LinkSpec> Links(ShortcutStory story)
    {
        var links = new List<LinkSpec>();
        links.AddRange(story.PullRequests.Select(pr => new LinkSpec(WorkLinkKind.Pr, pr.Url)));
        links.AddRange(story.Branches.Where(branch => branch.Url is not null).Select(branch => new LinkSpec(WorkLinkKind.Branch, branch.Url!)));
        links.AddRange(story.Commits.Where(commit => commit.Url is not null).Select(commit => new LinkSpec(WorkLinkKind.Other, commit.Url!)));
        links.AddRange(story.ExternalLinks.Where(url => url.Length is > 0 and <= 2048).Select(url => new LinkSpec(LinkKind(url), url)));
        links.AddRange(story.LinkedFiles.Select(file => new LinkSpec(WorkLinkKind.Other, file.Url)));
        return links.Where(link => link.Url.Length <= 2048).DistinctBy(link => (link.Kind, link.Url)).ToList();
    }

    /// <summary>The repositories the story's PRs, branches and commits live in, as <c>owner/repo</c>, most named first.</summary>
    public static IReadOnlyList<string> RepositoryCandidates(ShortcutStory story) =>
        story.PullRequests.Select(pr => pr.Url)
            .Concat(story.Branches.Select(branch => branch.Url ?? ""))
            .Concat(story.Commits.Select(commit => commit.Url ?? ""))
            .Concat(story.ExternalLinks.Where(url => LinkKind(url) != WorkLinkKind.Other))
            .Select(url => GitHubUrl().Match(url))
            .Where(match => match.Success)
            .Select(match => $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value.Replace(".git", "", StringComparison.OrdinalIgnoreCase)}")
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.First())
            .ToList();

    /// <summary>
    /// The item's repository: the owned repository its PRs/branches name (by <c>owner/repo</c> or by the
    /// repository name alone, ignoring case); otherwise the project's single owned repository; otherwise none.
    /// </summary>
    public static string? ChooseRepository(IReadOnlyList<string> candidates, IReadOnlyList<string> owned)
    {
        foreach (var candidate in candidates)
        {
            var name = candidate[(candidate.IndexOf('/') + 1)..];
            var match = owned.FirstOrDefault(repository => repository.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                ?? owned.FirstOrDefault(repository => repository.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? owned.FirstOrDefault(repository => repository.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return owned.Count == 1 ? owned[0] : null;
    }

    /// <summary>The stories that block <paramref name="story"/>: its <c>is blocked by</c> links and its sub-tasks (the parent waits for them).</summary>
    public static IReadOnlyList<long> Blockers(ShortcutStory story) =>
        story.StoryLinks.Where(link => IsBlocks(link.Verb) && link.ObjectId == story.Id && link.SubjectId != story.Id).Select(link => link.SubjectId)
            .Concat(story.SubTaskIds)
            .Distinct().ToList();

    private static bool IsBlocks(string verb) => verb.Equals("blocks", StringComparison.OrdinalIgnoreCase);

    public static string Alias(long storyId) => "sc-" + storyId.ToString(CultureInfo.InvariantCulture);

    public static MappedStory Map(ShortcutStory story, IReadOnlyDictionary<long, ShortcutState> states, IReadOnlyDictionary<string, ImportMemberMapping> members,
        IReadOnlyDictionary<string, ShortcutTeam> teams)
    {
        var state = states.GetValueOrDefault(story.WorkflowStateId);
        var status = Status(state);
        var blockers = Blockers(story);
        var comments = story.Comments.Select(comment =>
        {
            var (name, kind) = Author(comment.AuthorId, comment.Text, members);
            return new ImportedComment(comment.Id.ToString(CultureInfo.InvariantCulture), name, kind, comment.Text, comment.CreatedAt);
        }).ToList();

        // relates to / duplicates: a link noted on the item, naming the other story's alias (get_work resolves it).
        var relations = new List<(string Verb, long Other)>();
        foreach (var link in story.StoryLinks.Where(link => !IsBlocks(link.Verb)))
        {
            var subject = link.SubjectId == story.Id;
            var other = subject ? link.ObjectId : link.SubjectId;
            var verb = link.Verb.ToLowerInvariant();
            relations.Add((verb, other));
            var text = verb switch
            {
                "duplicates" => subject ? $"Duplicates {Alias(other)}." : $"Duplicated by {Alias(other)}.",
                _ => $"Relates to {Alias(other)}."
            };
            comments.Add(new ImportedComment($"rel:{link.Id}", ImportSources.Actor(ImportSources.Shortcut).Name, WorkActor.SystemKind,
                $"{text} (Shortcut story link, imported.)", story.CreatedAt));
        }

        // D26: Shortcut's blocked flag without a blocking story becomes Backlog with the reason.
        var blockedWithoutEdges = story.Blocked && blockers.Count == 0 && status is not (WorkStatus.Complete or WorkStatus.Closed);
        if (blockedWithoutEdges)
        {
            comments.Add(new ImportedComment("blocked-without-edges", ImportSources.Actor(ImportSources.Shortcut).Name, WorkActor.SystemKind,
                $"Imported as Backlog: Shortcut showed this story as blocked in '{state?.Name ?? "unknown"}', but no blocking story is linked to it (design D26).",
                story.UpdatedAt));
            status = WorkStatus.Backlog;
        }

        var owners = story.OwnerIds.Select(id => members.TryGetValue(id, out var owner) ? owner.Name : $"shortcut:{id}").ToList();
        var history = new ImportHistory(
            owners.Count == 0 ? null : owners,
            story.RequestedById is { } requester ? members.TryGetValue(requester, out var by) ? by.Name : $"shortcut:{requester}" : null,
            story.StartedAt, story.CompletedAt, state?.Name,
            story.TeamId is { } team && teams.TryGetValue(team, out var named) ? named.Name : null);

        // PR links that must never move the item later: every PR of a finished story, and the merged or
        // closed PRs of an open one. A PR still open in Shortcut stays live (S6 may move the item by it).
        var finished = status is WorkStatus.Complete or WorkStatus.Closed;
        var historical = story.PullRequests.Where(pr => finished || pr.Merged || pr.Closed).Select(pr => pr.Url)
            .Concat(finished ? story.ExternalLinks.Where(url => LinkKind(url) == WorkLinkKind.Pr) : [])
            .Distinct(StringComparer.Ordinal).ToList();

        return new MappedStory(
            story.Id,
            string.IsNullOrWhiteSpace(story.Name) ? Alias(story.Id) : story.Name.Trim(),
            Type(story.StoryType),
            status,
            story.Description,
            story.Labels,
            story.Tasks.Where(task => task.Description.Trim().Length > 0)
                // The checklist task's id names the criterion (SC10356), so a re-run merges criterion by criterion.
                .Select(task => new CriterionSpec("SC" + task.Id.ToString(CultureInfo.InvariantCulture), Cut(task.Description.Trim(), 2000),
                    task.Complete ? AcceptanceState.Met : AcceptanceState.Unmet, null)).ToList(),
            comments,
            Links(story),
            [Alias(story.Id)],
            new ImportedView(ImportSources.Shortcut, Alias(story.Id), story.AppUrl, history),
            story.CreatedAt,
            story.UpdatedAt,
            RepositoryCandidates(story),
            blockers,
            relations,
            blockedWithoutEdges,
            historical);
    }

    /// <summary>Feature documents of an epic: its linked Shortcut Docs, and its comments (a feature has no comment thread) with authors and times.</summary>
    public static IReadOnlyList<DocumentSpec> EpicDocuments(ShortcutEpic epic, IReadOnlyList<ShortcutDocument> documents, IReadOnlyDictionary<string, ImportMemberMapping> members)
    {
        var result = documents.Select(document => new DocumentSpec(document.Title.Trim().Length == 0 ? "Document" : document.Title.Trim(), document.Body)).ToList();
        if (epic.Comments.Count > 0)
        {
            var body = new StringBuilder("Comments on the Shortcut epic, oldest first (imported).\n");
            foreach (var comment in epic.Comments)
            {
                var (name, kind) = Author(comment.AuthorId, comment.Text, members);
                body.Append(CultureInfo.InvariantCulture, $"\n---\n\n**{name}** ({kind}), {comment.CreatedAt:yyyy-MM-dd HH:mm} UTC\n\n{comment.Text}\n");
            }

            result.Add(new DocumentSpec("Shortcut epic comments", body.ToString()));
        }

        return result;
    }

    public static ImportedView EpicImported(ShortcutEpic epic, IReadOnlyDictionary<string, ImportMemberMapping> members, IReadOnlyDictionary<string, ShortcutTeam> teams)
    {
        var owners = epic.OwnerIds.Select(id => members.TryGetValue(id, out var owner) ? owner.Name : $"shortcut:{id}").ToList();
        return new ImportedView(ImportSources.Shortcut, "sc-epic-" + epic.Id.ToString(CultureInfo.InvariantCulture), epic.AppUrl,
            new ImportHistory(owners.Count == 0 ? null : owners,
                epic.RequestedById is { } requester ? members.TryGetValue(requester, out var by) ? by.Name : $"shortcut:{requester}" : null,
                epic.StartedAt, epic.CompletedAt, epic.State,
                epic.TeamIds.FirstOrDefault() is { } team && teams.TryGetValue(team, out var named) ? named.Name : null));
    }

    public static string ReconcileDescription(string featureKey, long epicId) =>
        $"Feature {featureKey} was imported from Shortcut epic {epicId} with its requirements left in its description (unreconciled).\n\n" +
        "Read the feature's description and documents, extract its numbered requirements (E1…En) and acceptance tests, and write them as structured " +
        $"requirements and acceptance_tests with update_work on {featureKey}. Then give the feature's imported items inherits where the epic's story " +
        "text names them. The feature reads reconciled once it has structured requirements.";

    /// <summary>SHA-256 of the mapped object with the mapping version: equal hashes mean nothing to write.</summary>
    public static string Hash(object mapped) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(MappingVersion + "\n" + JsonSerializer.Serialize(mapped, WorkJson.Options))));

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>A proposed project key for a team: its name's letters and digits, upper-cased, 2–10 characters.</summary>
    public static string ProposedKey(string name)
    {
        var letters = new string(name.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (letters.Length == 0 || !char.IsAsciiLetter(letters[0]))
            letters = "T" + letters;
        if (letters.Length < 2)
            letters += "X";
        return letters.Length <= 10 ? letters : letters[..10];
    }
}
