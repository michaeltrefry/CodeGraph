using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

/// <summary>A work tool: its name, its argument type (and so its schema) and its MCP hints.</summary>
public sealed record WorkToolDefinition(string Name, string Title, Type ArgumentsType, string Description, bool ReadOnly, bool Idempotent);

/// <summary>
/// The six work tools of S1. Each is reachable over MCP (<see cref="WorkMcpTool"/>) and REST
/// (WorkController) with the same arguments, read by <see cref="WorkJson.Parse{T}"/>.
/// Projects are created and changed through the same tools (<c>create_work(new_project)</c>,
/// <c>update_work</c> on a project key) rather than through tools of their own, which keeps the
/// new surface at six tools; each kind keeps a strict nested schema.
/// </summary>
public static class WorkTools
{
    public const string CreateWork = "create_work";
    public const string GetWork = "get_work";
    public const string FindWork = "find_work";
    public const string UpdateWork = "update_work";
    public const string NextWork = "next_work";
    public const string Changes = "changes";

    private const string Errors =
        " Refused calls change nothing and answer {code, message}: invalid_argument (an unknown, misspelt or mistyped argument, listing the valid ones; or a value breaking a rule), " +
        "not_found, conflict, already_claimed, cycle, busy (retry the same call).";

    public static readonly IReadOnlyList<WorkToolDefinition> All =
    [
        new(CreateWork, "Create Work", typeof(CreateWorkRequest),
            "Create work in a Project: a new_project; or, in project, one feature (with documents), one item, or a whole plan. " +
            "A plan is all-or-nothing: features (with documents) and items with caller refs, an item naming its feature by ref and its blockers by ref in blocked_by; " +
            "a malformed item, a duplicate ref, an unknown ref or a dependency cycle (refused cycle) creates nothing. A plan holds at most 200 items, 20 features, " +
            "20 documents, 500 dependencies and 2 MB of JSON, and needs idempotency_key. Answers the created object (with created), or for a plan the id and key " +
            "of every ref. Items get keys <KEY>-<n>, features <KEY>-F<n>, allocated in order without gaps. A repeated idempotency_key creates nothing and answers " +
            "what it created, with created false." + Errors,
            ReadOnly: false, Idempotent: true),
        new(GetWork, "Get Work", typeof(GetWorkRequest),
            "Get one project, feature or item by key (SW, SW-F12, SW-142) or id, in full: an item with its blockers and dependents (and their status), " +
            "derived blocked state (blocked_reason waiting, or cancelled_prerequisite when a blocker is closed), live claim, links and comments; a feature with " +
            "its documents and item counts; a project with its goals, repositories and item counts. Every object carries version, the expected_version of update_work." + Errors,
            ReadOnly: true, Idempotent: true),
        new(FindWork, "Find Work", typeof(FindWorkRequest),
            "List items (default), features or projects: one line per row (id, key, title, status, priority, blocked, claimed_by, version) unless detail is full. " +
            "Items and features need project. Closed items are left out unless status names closed. ready true lists what next_work could hand out now. " +
            "Pages of limit rows with next_cursor (left out on the last page), in key order." + Errors,
            ReadOnly: true, Idempotent: true),
        new(UpdateWork, "Update Work", typeof(UpdateWorkRequest),
            "Change a project, feature or item, guarded by expected_version: of concurrent calls with one version exactly one succeeds, the others are refused conflict " +
            "with the current object in current. Only the fields passed change; empty an optional one with clear. Each kind takes its own fields (a field of another " +
            "kind is refused invalid_argument listing the valid ones). On an item, one call can also change its status (any transition; complete or closed releases " +
            "the claim), add or remove blockers (only a complete blocker stops blocking; a closed one keeps its dependents blocked), add or remove links, add a comment, " +
            "and claim, renew or release it (claims are advisory and do not stop other writes). Answers the object as it is after the write, with its new version." + Errors,
            ReadOnly: false, Idempotent: false),
        new(NextWork, "Next Work", typeof(NextWorkRequest),
            "Claim the best item available in a project for the caller: status ready, every blocker complete (a closed blocker does not count), no live claim; " +
            "ordered by priority, then order, then creation. The claim belongs to the caller's identity and marker and lapses after ttl_seconds unless renewed. " +
            "Safe to retry: without another true it answers the caller's existing live claim under marker instead of a second one, and a repeated idempotency_key " +
            "answers the same allocation (reused true). Concurrent callers never get the same item. No item: item is left out and reason is no_ready_work. " +
            "next_work does not change the status; move the item to in_progress with update_work." + Errors,
            ReadOnly: false, Idempotent: true),
        new(Changes, "Work Changes", typeof(ChangesRequest),
            "Read the change feed: every write to projects, features and items, exactly once and in commit order, as {items, next_cursor}. Call without cursor to get " +
            "the cursor of the newest entry (no entries), or from_start true for the oldest; then pass next_cursor back each time. wait_seconds long-polls for new entries. " +
            "Each entry is {sequence, project_id, project_key, entity, entity_id, entity_key, kind, actor, changes [{field, old, new}], created_at}; kinds are " +
            "project_created, project_updated, feature_created, feature_updated, item_created, item_updated, claimed. A text over 16384 characters is cut and marked " +
            "old_truncated / new_truncated." + Errors,
            ReadOnly: true, Idempotent: true)
    ];

    public static WorkToolDefinition? Find(string name) => All.FirstOrDefault(tool => tool.Name == name);
}
