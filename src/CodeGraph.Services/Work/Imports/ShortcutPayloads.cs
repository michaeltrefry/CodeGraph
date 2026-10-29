using System.Globalization;
using System.Text.Json;

namespace CodeGraph.Services.Work.Imports;

// Shortcut's REST v3 objects as the importer reads them (sc-27876). Parsed leniently from either the
// REST shape (group_id, group_ids, labels [{name}], members with a profile) or the Shortcut MCP
// shape (team_id, team_ids), so recorded payloads of both kinds feed the same mapping.

public sealed record ShortcutTeam(string Id, string Name, string? MentionName);

public sealed record ShortcutMember(string Id, string Name, string? MentionName, string? Email);

public sealed record ShortcutState(long Id, string Name, string Type);

public sealed record ShortcutComment(long Id, string? AuthorId, string Text, DateTime CreatedAt);

public sealed record ShortcutTask(long Id, string Description, bool Complete, int Position);

/// <summary>A story link: <see cref="SubjectId"/> <see cref="Verb"/> <see cref="ObjectId"/> (blocks, relates to, duplicates).</summary>
public sealed record ShortcutStoryLink(long Id, long SubjectId, long ObjectId, string Verb);

public sealed record ShortcutBranch(string Name, string? Url);

public sealed record ShortcutPullRequest(long Number, string Url, string? Title, bool Merged, bool Closed);

public sealed record ShortcutCommit(string Hash, string? Url);

public sealed record ShortcutFile(long Id, string Name, string ContentType, long Size, string Url, string? UploaderId, DateTime CreatedAt);

public sealed record ShortcutLinkedFile(long Id, string Name, string Url);

public sealed record ShortcutDocument(string Id, string Title, string Body);

public sealed record ShortcutEpic(
    long Id,
    string Name,
    string? Description,
    bool Archived,
    bool Done,
    string? State,
    IReadOnlyList<string> TeamIds,
    IReadOnlyList<string> OwnerIds,
    string? RequestedById,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ShortcutComment> Comments,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? AppUrl,
    int RelatedDocuments);

public sealed record ShortcutStorySummary(long Id, DateTime UpdatedAt);

public sealed record ShortcutStory(
    long Id,
    string Name,
    string? Description,
    string StoryType,
    long WorkflowStateId,
    bool Archived,
    bool Blocked,
    string? TeamId,
    long? EpicId,
    long? ParentStoryId,
    IReadOnlyList<long> SubTaskIds,
    IReadOnlyList<string> OwnerIds,
    string? RequestedById,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ShortcutTask> Tasks,
    IReadOnlyList<ShortcutComment> Comments,
    IReadOnlyList<ShortcutStoryLink> StoryLinks,
    IReadOnlyList<ShortcutBranch> Branches,
    IReadOnlyList<ShortcutPullRequest> PullRequests,
    IReadOnlyList<ShortcutCommit> Commits,
    IReadOnlyList<string> ExternalLinks,
    IReadOnlyList<ShortcutFile> Files,
    IReadOnlyList<ShortcutLinkedFile> LinkedFiles,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? AppUrl,
    string? Estimate);

public static class ShortcutPayload
{
    /// <summary>The object itself, or the one under <c>story</c> / <c>epic</c> when the payload wraps it (as the Shortcut MCP does).</summary>
    public static JsonElement Unwrap(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : element;

    public static ShortcutTeam Team(JsonElement e) => new(Str(e, "id") ?? "", Str(e, "name") ?? "", Str(e, "mention_name"));

    public static ShortcutMember Member(JsonElement e)
    {
        var profile = e.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.Object ? p : e;
        return new ShortcutMember(Str(e, "id") ?? "", Str(profile, "name") ?? Str(profile, "mention_name") ?? "", Str(profile, "mention_name"), Str(profile, "email_address"));
    }

    /// <summary>The states of every workflow in a list of workflows, or of one workflow.</summary>
    public static IEnumerable<ShortcutState> States(JsonElement workflows)
    {
        var list = workflows.ValueKind == JsonValueKind.Array ? workflows.EnumerateArray().ToList() : [workflows];
        foreach (var workflow in list)
            foreach (var state in Array(workflow, "states"))
                yield return new ShortcutState(Long(state, "id") ?? 0, Str(state, "name") ?? "", Str(state, "type") ?? "");
    }

    public static ShortcutEpic Epic(JsonElement raw)
    {
        var e = Unwrap(raw, "epic");
        var teams = Strings(e, "group_ids").Concat(Strings(e, "team_ids")).ToList();
        if (Str(e, "group_id") is { } group)
            teams.Add(group);
        if (Str(e, "team_id") is { } team)
            teams.Add(team);
        var stateType = Str(e, "state") ?? "";
        var relatedDocuments = e.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object ? (int)(Long(stats, "num_related_documents") ?? 0) : 0;
        return new ShortcutEpic(
            Long(e, "id") ?? 0, Str(e, "name") ?? "", Str(e, "description"), Bool(e, "archived"),
            Bool(e, "completed") || stateType.Equals("done", StringComparison.OrdinalIgnoreCase), stateType,
            teams.Distinct().ToList(), Strings(e, "owner_ids").ToList(), Str(e, "requested_by_id"), Labels(e),
            Comments(e), Time(e, "created_at") ?? DateTime.UnixEpoch, Time(e, "updated_at") ?? Time(e, "created_at") ?? DateTime.UnixEpoch,
            Time(e, "started_at"), Time(e, "completed_at"), Str(e, "app_url"), relatedDocuments);
    }

    public static ShortcutStory Story(JsonElement raw)
    {
        var e = Unwrap(raw, "story");
        var subtasks = Longs(e, "sub_task_story_ids").Concat(Longs(e, "sub_task_ids")).Distinct().ToList();
        return new ShortcutStory(
            Long(e, "id") ?? 0, Str(e, "name") ?? "", Str(e, "description"), Str(e, "story_type") ?? "feature", Long(e, "workflow_state_id") ?? 0,
            Bool(e, "archived"), Bool(e, "blocked"), Str(e, "group_id") ?? Str(e, "team_id"), Long(e, "epic_id"), Long(e, "parent_story_id"), subtasks,
            Strings(e, "owner_ids").ToList(), Str(e, "requested_by_id"), Labels(e),
            Array(e, "tasks").Select(t => new ShortcutTask(Long(t, "id") ?? 0, Str(t, "description") ?? "", Bool(t, "complete"), (int)(Long(t, "position") ?? 0)))
                .OrderBy(t => t.Position).ThenBy(t => t.Id).ToList(),
            Comments(e),
            Array(e, "story_links").Select(l => new ShortcutStoryLink(Long(l, "id") ?? 0, Long(l, "subject_id") ?? 0, Long(l, "object_id") ?? 0, Str(l, "verb") ?? "")).ToList(),
            Array(e, "branches").Where(b => !Bool(b, "deleted")).Select(b => new ShortcutBranch(Str(b, "name") ?? "", Str(b, "url"))).ToList(),
            Array(e, "pull_requests").Select(p => new ShortcutPullRequest(Long(p, "number") ?? 0, Str(p, "url") ?? "", Str(p, "title"), Bool(p, "merged"), Bool(p, "closed")))
                .Where(p => p.Url.Length > 0).ToList(),
            Array(e, "commits").Select(c => new ShortcutCommit(Str(c, "hash") ?? "", Str(c, "url"))).ToList(),
            Strings(e, "external_links").ToList(),
            Array(e, "files").Select(f => new ShortcutFile(Long(f, "id") ?? 0, Str(f, "name") ?? Str(f, "filename") ?? "file", Str(f, "content_type") ?? "application/octet-stream",
                Long(f, "size") ?? 0, Str(f, "url") ?? "", Str(f, "uploader_id"), Time(f, "created_at") ?? DateTime.UnixEpoch)).ToList(),
            Array(e, "linked_files").Select(f => new ShortcutLinkedFile(Long(f, "id") ?? 0, Str(f, "name") ?? "", Str(f, "url") ?? "")).Where(f => f.Url.Length > 0).ToList(),
            Time(e, "created_at") ?? DateTime.UnixEpoch, Time(e, "updated_at") ?? Time(e, "created_at") ?? DateTime.UnixEpoch,
            Time(e, "started_at"), Time(e, "completed_at"), Str(e, "app_url"),
            e.TryGetProperty("estimate", out var estimate) && estimate.ValueKind == JsonValueKind.Number ? estimate.GetRawText() : null);
    }

    public static ShortcutDocument Document(JsonElement e) =>
        new(Str(e, "id") ?? "", Str(e, "title") ?? Str(e, "name") ?? "Document",
            Str(e, "content_markdown") ?? Str(e, "markdown") ?? Str(e, "content") ?? Str(e, "body") ?? "");

    private static List<ShortcutComment> Comments(JsonElement e)
    {
        var result = new List<ShortcutComment>();
        void Walk(JsonElement owner)
        {
            foreach (var c in Array(owner, "comments"))
            {
                if (!Bool(c, "deleted") && Str(c, "text") is { Length: > 0 } text)
                    result.Add(new ShortcutComment(Long(c, "id") ?? 0, Str(c, "author_id"), text, Time(c, "created_at") ?? DateTime.UnixEpoch));
                // Epic comments carry their replies.
                Walk(c);
            }
        }

        Walk(e);
        return result.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
    }

    private static List<string> Labels(JsonElement e) =>
        Array(e, "labels").Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : Str(l, "name")).OfType<string>().Where(l => l.Length > 0).ToList();

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        Array(e, name).Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!);

    private static IEnumerable<long> Longs(JsonElement e, string name) =>
        Array(e, name).Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetInt64());

    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    public static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64(),
                JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            }
            : null;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static DateTime? Time(JsonElement e, string name) =>
        Str(e, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
}
