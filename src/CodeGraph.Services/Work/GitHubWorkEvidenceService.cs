using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work;

/// <summary>Reads GitHub webhook payloads into work evidence (sc-27870). Registered always; records only when the work store exists.</summary>
public interface IGitHubWorkEvidenceService
{
    /// <summary>
    /// Handles one signature-verified delivery. Events that are not evidence (or not in an action
    /// that is) record nothing. <paramref name="repositoryNames"/> are extra names the repository
    /// goes by in CodeGraph (its indexed name).
    /// </summary>
    Task<GitHubEvidenceResult> HandleAsync(string? eventName, byte[] body, string? deliveryId, IReadOnlyList<string> repositoryNames, CancellationToken ct = default);
}

public sealed class GitHubWorkEvidenceService : IGitHubWorkEvidenceService
{
    private readonly Func<IWorkStore?> storeAccessor;
    private readonly IReadOnlyCollection<string> ownerLogins;

    // IWorkStore exists with the MariaDB provider only: resolve it per call, so the webhook works
    // (indexing only) in a deployment without one.
    public GitHubWorkEvidenceService(IServiceProvider services, IOptions<RepositorySourceOptions> options)
        : this(() => services.GetService<IWorkStore>(), options.Value.GitHub.OwnerLogins)
    {
    }

    /// <summary>The service over a given store (tests, tools).</summary>
    public static GitHubWorkEvidenceService ForStore(IWorkStore store, string ownerLogins = "") =>
        new(() => store, ownerLogins);

    private GitHubWorkEvidenceService(Func<IWorkStore?> storeAccessor, string ownerLogins)
    {
        this.storeAccessor = storeAccessor;
        this.ownerLogins = ownerLogins.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<GitHubEvidenceResult> HandleAsync(string? eventName, byte[] body, string? deliveryId, IReadOnlyList<string> repositoryNames, CancellationToken ct = default)
    {
        if (storeAccessor() is not { } store)
            return GitHubEvidenceResult.None;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return GitHubEvidenceResult.None;
        }

        using (document)
        {
            // A redelivery keeps GitHub's delivery id; without the header the payload itself names the delivery.
            var delivery = string.IsNullOrWhiteSpace(deliveryId)
                ? "body:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body))
                : deliveryId.Trim();
            var spec = Build(eventName, document.RootElement, delivery, repositoryNames);
            return spec is null ? GitHubEvidenceResult.None : await store.RecordGitHubEvidenceAsync(spec, ct);
        }
    }

    private GitHubEvidenceSpec? Build(string? eventName, JsonElement root, string delivery, IReadOnlyList<string> extraNames)
    {
        var repository = Obj(root, "repository");
        var names = new List<string>();
        AddName(names, Str(repository, "full_name"));
        foreach (var name in extraNames)
            AddName(names, name);
        AddName(names, Str(repository, "name"));
        var htmlUrl = Str(repository, "html_url")?.TrimEnd('/');
        var action = Str(root, "action");
        var sender = Str(Obj(root, "sender"), "login");

        switch (eventName?.ToLowerInvariant())
        {
            case "push":
            {
                var reference = Str(root, "ref");
                var after = Str(root, "after");
                if (reference is null || !reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                    || Bool(root, "deleted") || string.IsNullOrWhiteSpace(after) || after.All(c => c == '0'))
                    return null;

                var branch = reference["refs/heads/".Length..];
                var pusher = sender ?? Str(Obj(root, "pusher"), "name");
                if (pusher is null || htmlUrl is null)
                    return null;

                return new GitHubEvidenceSpec(delivery, GitHubEvidenceKind.Push, names, [branch], pusher,
                    Branch: branch, BranchUrl: BranchUrl(htmlUrl, branch), Url: $"{htmlUrl}/commit/{after}", ExternalId: after,
                    Body: CommitList(root));
            }

            case "pull_request":
            {
                var pull = Obj(root, "pull_request");
                if (pull.ValueKind != JsonValueKind.Object || Number(pull, "number") is not { } number)
                    return null;

                var state = Str(pull, "state");
                var merged = Bool(pull, "merged");
                GitHubEvidenceKind kind;
                string? login;
                switch (action)
                {
                    case "opened" or "reopened" or "edited" or "synchronize" or "ready_for_review" when state == "open":
                        kind = GitHubEvidenceKind.PullRequestOpen;
                        login = sender ?? Str(Obj(pull, "user"), "login");
                        break;
                    case "closed":
                        kind = merged ? GitHubEvidenceKind.PullRequestMerged : GitHubEvidenceKind.PullRequestClosed;
                        login = (merged ? Str(Obj(pull, "merged_by"), "login") : null) ?? sender ?? Str(Obj(pull, "user"), "login");
                        break;
                    default:
                        return null;
                }

                if (login is null)
                    return null;

                var head = Obj(pull, "head");
                var branch = Str(head, "ref");
                var headRepository = Str(Obj(head, "repo"), "html_url")?.TrimEnd('/') ?? htmlUrl;
                var title = Str(pull, "title");
                var description = Str(pull, "body");
                var actorIsBot = IsBot(login, Str(Obj(root, "sender"), "type"));
                return new GitHubEvidenceSpec(delivery, kind, names, [branch ?? "", title ?? "", description ?? ""], login,
                    Branch: branch, BranchUrl: headRepository is null || branch is null ? null : BranchUrl(headRepository, branch),
                    PullRequestUrl: Str(pull, "html_url"), PullRequestNumber: number, Title: title, Description: description,
                    Url: Str(pull, "html_url"),
                    ExternalId: kind == GitHubEvidenceKind.PullRequestMerged ? Str(pull, "merge_commit_sha") : null,
                    Source: actorIsBot ? GitHubEvidenceSources.Bot : GitHubEvidenceSources.Human);
            }

            case "pull_request_review":
            {
                var pull = Obj(root, "pull_request");
                var review = Obj(root, "review");
                if (action is not ("submitted" or "edited") || pull.ValueKind != JsonValueKind.Object || review.ValueKind != JsonValueKind.Object
                    || Id(review, "id") is not { } reviewId)
                    return null;

                var author = Obj(review, "user");
                return Conversation(delivery, GitHubEvidenceKind.Review, names, repository, pull, author, Str(review, "author_association"),
                    reviewId, Str(review, "html_url"), Str(review, "body"), Str(review, "state")?.ToLowerInvariant(), null);
            }

            case "pull_request_review_comment":
            {
                var pull = Obj(root, "pull_request");
                var comment = Obj(root, "comment");
                if (action is not ("created" or "edited") || pull.ValueKind != JsonValueKind.Object || comment.ValueKind != JsonValueKind.Object
                    || Id(comment, "id") is not { } commentId)
                    return null;

                var path = Str(comment, "path");
                var line = Number(comment, "line") ?? Number(comment, "original_line");
                return Conversation(delivery, GitHubEvidenceKind.ReviewComment, names, repository, pull, Obj(comment, "user"),
                    Str(comment, "author_association"), commentId, Str(comment, "html_url"), Str(comment, "body"), null,
                    path is null ? null : line is { } at ? $"{path}:{at}" : path);
            }

            case "issue_comment":
            {
                var issue = Obj(root, "issue");
                var comment = Obj(root, "comment");
                // Only comments on pull requests: an issue carries a pull_request member when it is one.
                if (action is not ("created" or "edited") || issue.ValueKind != JsonValueKind.Object || comment.ValueKind != JsonValueKind.Object
                    || Obj(issue, "pull_request").ValueKind != JsonValueKind.Object || Id(comment, "id") is not { } commentId)
                    return null;

                var asPull = Obj(issue, "pull_request");
                var url = Str(asPull, "html_url") ?? Str(issue, "html_url");
                return Conversation(delivery, GitHubEvidenceKind.IssueComment, names, repository, issue, Obj(comment, "user"),
                    Str(comment, "author_association"), commentId, Str(comment, "html_url"), Str(comment, "body"), null, null,
                    pullUrl: url);
            }

            default:
                return null;
        }
    }

    // A review, review comment or conversation comment: evidence with its author's source and priority.
    private GitHubEvidenceSpec? Conversation(string delivery, GitHubEvidenceKind kind, List<string> names, JsonElement repository, JsonElement pull,
        JsonElement author, string? association, string externalId, string? url, string? body, string? state, string? location, string? pullUrl = null)
    {
        var login = Str(author, "login");
        if (login is null)
            return null;

        var number = Number(pull, "number");
        var pullRequestUrl = pullUrl ?? Str(pull, "html_url");
        var head = Obj(pull, "head");
        var title = Str(pull, "title");
        var description = Str(pull, "body");
        var bot = IsBot(login, Str(author, "type"));
        var ownerLogin = Str(Obj(repository, "owner"), "login");
        var owner = !bot && (string.Equals(login, ownerLogin, StringComparison.OrdinalIgnoreCase) || ownerLogins.Contains(login)
                             || string.Equals(association, "OWNER", StringComparison.OrdinalIgnoreCase));
        return new GitHubEvidenceSpec(delivery, kind, names, [Str(head, "ref") ?? "", title ?? "", description ?? ""], login,
            Branch: Str(head, "ref"), PullRequestUrl: pullRequestUrl, PullRequestNumber: number, Title: title,
            Url: url ?? pullRequestUrl, ExternalId: externalId, ReviewState: state, Location: location, Body: body,
            Source: bot ? GitHubEvidenceSources.Bot : GitHubEvidenceSources.Human,
            Priority: owner ? GitHubEvidenceSources.HighPriority : GitHubEvidenceSources.NormalPriority);
    }

    private static bool IsBot(string login, string? type) =>
        string.Equals(type, "Bot", StringComparison.OrdinalIgnoreCase) || login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

    private static string BranchUrl(string repositoryUrl, string branch) => $"{repositoryUrl}/tree/{branch}";

    private static void AddName(List<string> names, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            names.Add(name.Trim());
    }

    /// <summary>The push's commits, one per line as "sha first-line-of-message", the newest 20 (GitHub lists oldest first, so newest last).</summary>
    private static string? CommitList(JsonElement root)
    {
        if (!root.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array)
            return null;

        var lines = commits.EnumerateArray()
            .Select(commit => (Id: Str(commit, "id"), Message: Str(commit, "message")))
            .Where(commit => !string.IsNullOrWhiteSpace(commit.Id))
            .Select(commit =>
            {
                var first = (commit.Message ?? "").Split('\n', 2)[0].Trim();
                return $"{commit.Id} {(first.Length <= 120 ? first : first[..119] + "…")}".TrimEnd();
            })
            .ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines.Skip(Math.Max(0, lines.Count - 20)));
    }

    private static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Str(JsonElement element, string name) =>
        Obj(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        Obj(element, name) is { ValueKind: JsonValueKind.True };

    private static int? Number(JsonElement element, string name) =>
        Obj(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    // Review and comment ids are 64-bit numbers.
    private static string? Id(JsonElement element, string name) =>
        Obj(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var id) ? id.ToString() : null;
}
