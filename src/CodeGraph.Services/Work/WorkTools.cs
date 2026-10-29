using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

/// <summary>A work tool: its name, its argument type (and so its schema) and its MCP hints.</summary>
public sealed record WorkToolDefinition(string Name, string Title, Type ArgumentsType, string Description, bool ReadOnly, bool Idempotent);

/// <summary>
/// The work tools: the six of S1 and the three session tools of S2 (start_session, end_session,
/// get_briefing). Each is reachable over MCP (<see cref="WorkMcpTool"/>) and REST
/// (WorkController) with the same arguments, read by <see cref="WorkJson.Parse{T}"/>.
/// Projects are created and changed through the same tools (<c>create_work(new_project)</c>,
/// <c>update_work</c> on a project key) rather than through tools of their own; each kind keeps a
/// strict nested schema.
/// </summary>
public static class WorkTools
{
    public const string CreateWork = "create_work";
    public const string GetWork = "get_work";
    public const string FindWork = "find_work";
    public const string UpdateWork = "update_work";
    public const string NextWork = "next_work";
    public const string Changes = "changes";
    public const string StartSession = "start_session";
    public const string EndSession = "end_session";
    public const string GetBriefing = "get_briefing";
    public const string Record = "record";
    public const string Recall = "recall";
    public const string ImportKnowledge = "import_knowledge";

    private const string SavePoints =
        " Save points: decisions, attempts, corrections (lessons) and findings (facts, gotchas, constraints) are stored as entries of the project's knowledge " +
        "in the same transaction as the write and echoed in recorded; recall and other sessions' briefings return them.";

    private const string Errors =
        " Refused calls change nothing and answer {code, message}: invalid_argument (an unknown, misspelt or mistyped argument, listing the valid ones; or a value breaking a rule), " +
        "not_found, conflict, already_claimed, cycle, busy (retry the same call), claim_lost (a session's claim on the item lapsed or passed on: names the holder now).";

    private const string Sessions =
        " Pass session_id (from start_session) to renew the session's heartbeat, attribute the call to the session and bind claims to it.";

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
            "derived blocked state (blocked_reason waiting, or cancelled_prerequisite when a blocker is closed), live claim, links, comments, its latest handoff and handoff_count (detail full: every handoff, newest first); a feature with " +
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
            "record a handoff, and claim, renew or release it (claims are advisory and do not stop other writes). With session_id a claim is the session's and lasts " +
            "while the session is live (leave marker and ttl_seconds out); a retried claim the session already holds answers it. A session whose claim on the item " +
            "lapsed is refused claim_lost until it reclaims the unclaimed item (claim) or lets go of it (release_claim). Answers the object as it is after the write, " +
            "with its new version." + SavePoints + Sessions + Errors,
            ReadOnly: false, Idempotent: false),
        new(NextWork, "Next Work", typeof(NextWorkRequest),
            "Claim the best item available in a project for the caller. First a recoverable item (in_progress, every blocker complete, its claim lapsed or released), " +
            "answered with recovery: the previous holder, branch and PR links, the latest handoff (or the previous holder's last change when it left none); then a " +
            "ready item (every blocker complete, a closed blocker does not count, no live claim), ordered by priority, then order, then creation. With session_id the " +
            "claim is the session's and lasts while the session is live; without it, it belongs to the caller's identity and marker and lapses after ttl_seconds " +
            "unless renewed. Safe to retry: without another true it answers the caller's existing live claim instead of a second one, and a repeated idempotency_key " +
            "answers the same allocation (reused true), or claim_lost when that claim lapsed and passed on. Concurrent callers never get the same item. No item: item " +
            "is left out and reason is no_ready_work. next_work does not change the status; move a ready item to in_progress with update_work." + Sessions + Errors,
            ReadOnly: false, Idempotent: true),
        new(Changes, "Work Changes", typeof(ChangesRequest),
            "Read the change feed: every write to projects, features and items, exactly once and in commit order, as {items, next_cursor}. Call without cursor to get " +
            "the cursor of the newest entry (no entries), or from_start true for the oldest; then pass next_cursor back each time. wait_seconds long-polls for new entries. " +
            "Each entry is {sequence, project_id, project_key, entity, entity_id, entity_key, kind, actor, changes [{field, old, new}], created_at}; kinds are " +
            "project_created, project_updated, feature_created, feature_updated, item_created, item_updated, claimed, recovered. The actor names the identity and, for a " +
            "session, its session_id, harness, model and host. A text over 16384 characters is cut and marked old_truncated / new_truncated." + Errors,
            ReadOnly: true, Idempotent: true),
        new(StartSession, "Start Session", typeof(StartSessionRequest),
            "Register this agent as a session of a project (harness, model, host, repository, branch, worktree, intent) and answer the session and its briefing. " +
            "Pass the session's id as session_id to every work tool: each call is its heartbeat, and a session silent past the idle threshold (15 minutes by " +
            "default) is presumed dead and its claims lapse. The briefing is data within budget_tokens (default 1500): the project and its goals, the items " +
            "this session holds with their latest handoff, the other live sessions of the project and what they hold, one suggested next item (recoverable " +
            "first), and knowledge: the active decisions, constraints, lessons and gotchas linked to its items, scoped to paths (passed here), to its repository, " +
            "or project-wide; empty sections are left out and truncated names sections the budget shortened." + Errors,
            ReadOnly: false, Idempotent: false),
        new(EndSession, "End Session", typeof(EndSessionRequest),
            "End a session: record the handoff (done, decisions, open issues, corrections, next action, branch, commit) on item, or on the one item the session " +
            "holds, and release every claim of the session. An in-progress item it released is then recoverable by the next session, with the handoff. Answers " +
            "the ended session, the released items and the handoff. Ending an ended session changes nothing and answers it." + SavePoints + Errors,
            ReadOnly: false, Idempotent: true),
        new(GetBriefing, "Get Briefing", typeof(GetBriefingRequest),
            "Fetch the session's briefing again (for example after context compaction), optionally including one item (focus) with its latest handoff. Same " +
            "sections and budget rules as start_session; paths adds the knowledge scoped to them. Renews the session's heartbeat." + Errors,
            ReadOnly: false, Idempotent: true),
        new(Record, "Record Knowledge", typeof(RecordRequest),
            "Write the project's shared knowledge when no work item is involved (with an item, use the save-point fields of update_work or end_session). " +
            "entry: a new goal, decision, constraint, fact, gotcha, attempt, architecture note or lesson, scoped to the project and optionally to repositories, " +
            "paths/globs and graph nodes, with confidence; supersedes replaces an active entry (it becomes superseded, linked to the new one). revise: a new " +
            "revision of an active entry. retract: mark one wrong (kept, status retracted). verify: re-baseline it against its files. Entries are never edited " +
            "in place; every write adds a revision. The indexed files in an entry's paths and nodes are what it is verified against: when one of them changes " +
            "in a later index, the entry reads possibly_stale until it is verified, revised or superseded. Synchronous: answers the stored entry. record " +
            "(entry, supersede, revise, retract, verify) writes no changes entry: the change feed covers projects, features and items; save points stored by " +
            "update_work on one of those appear in its entry as a knowledge change." + Sessions + Errors,
            ReadOnly: false, Idempotent: false),
        new(Recall, "Recall Knowledge", typeof(RecallRequest),
            "Search the project's knowledge: by kind, repository, paths (an entry's path or glob covering one, or lying below one), graph nodes, item, " +
            "lesson activity and source, words, status (default active) and possibly_stale. Rows are small (id, kind, summary, confidence, version, and " +
            "possibly_stale / status / made_autonomously / item when they apply); detail full answers whole entries (at most 10 a page); id answers one entry " +
            "(detail full adds its revisions). Pages of limit (default 20, at most 50) newest first, with next_cursor." + Sessions + Errors,
            ReadOnly: true, Idempotent: true),
        new(ImportKnowledge, "Import Knowledge", typeof(ImportKnowledgeRequest),
            "Import native harness memory (such as Claude Code memory files) into the project's knowledge, in two steps. The importing agent verifies each " +
            "candidate against the repository itself and passes the result (verified, stale, unverifiable, personal). The dry run (confirm false, the default) " +
            "checks each candidate's scope (repositories and nodes of the project) and looks for duplicates (an active entry of the same kind with the same " +
            "normalized summary, or an earlier candidate), answers an outcome per candidate (would_store, duplicate, out_of_scope, personal, stale, invalid) " +
            "and a preview_token, and stores nothing. confirm true with accept (indexes) and that preview_token stores exactly the accepted candidates, all " +
            "or none, each with its source_file; a candidate that is not would_store now refuses the call. Source files are never changed." + Sessions + Errors,
            ReadOnly: false, Idempotent: false)
    ];

    public static WorkToolDefinition? Find(string name) => All.FirstOrDefault(tool => tool.Name == name);
}
