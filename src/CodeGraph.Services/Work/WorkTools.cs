using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

/// <summary>A work tool: its name, its argument type (and so its schema) and its MCP hints.</summary>
public sealed record WorkToolDefinition(string Name, string Title, Type ArgumentsType, string Description, bool ReadOnly, bool Idempotent);

/// <summary>
/// The work tools: the six of S1, the three session tools of S2 (start_session, end_session,
/// get_briefing) and sync_plan of S4. Each is reachable over MCP (<see cref="WorkMcpTool"/>) and REST
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
    public const string SyncPlan = "sync_plan";

    private const string Errors =
        " Refused calls change nothing and answer {code, message}: invalid_argument (an unknown, misspelt or mistyped argument, listing the valid ones; or a value breaking a rule), " +
        "not_found, conflict, already_claimed, cycle, busy (retry the same call), claim_lost (a session's claim on the item lapsed or passed on: names the holder now).";

    private const string Sessions =
        " Pass session_id (from start_session) to renew the session's heartbeat, attribute the call to the session and bind claims to it." +
        " A response may carry notices for the caller, such as changed_by_replan when a re-plan edited or closed an item it holds.";

    public static readonly IReadOnlyList<WorkToolDefinition> All =
    [
        new(CreateWork, "Create Work", typeof(CreateWorkRequest),
            "Create work in a Project: a new_project; or, in project, one feature (with documents), one item, or a whole plan. " +
            "Features take goals, numbered requirements (E1..En) and acceptance_tests; items take goal, 1 to 3 acceptance_criteria, inherits (requirements of their feature) and touches. " +
            "A plan is all-or-nothing: features (with documents) and items with caller refs (kept, so sync_plan can re-plan by them), an item naming its feature by ref and its blockers by ref in blocked_by; " +
            "a malformed item, a duplicate ref, an unknown ref or a dependency cycle (refused cycle) creates nothing. A plan holds at most 200 items, 20 features, " +
            "20 documents, 500 dependencies and 2 MB of JSON, and needs idempotency_key. Answers the created object (with created), or for a plan the id and key " +
            "of every ref. Items get keys <KEY>-<n>, features <KEY>-F<n>, allocated in order without gaps. A repeated idempotency_key creates nothing and answers " +
            "what it created, with created false." + Errors,
            ReadOnly: false, Idempotent: true),
        new(GetWork, "Get Work", typeof(GetWorkRequest),
            "Get one project, feature or item by key (SW, SW-F12, SW-142) or id, in full: an item with its blockers and dependents (and their status), " +
            "derived blocked state (blocked_reason waiting, or cancelled_prerequisite when a blocker is closed), live claim, links, comments, its latest handoff and handoff_count (detail full: every handoff, newest first); " +
            "an item is also its own brief: ref, goal, acceptance_criteria (state met or unmet, evidence), inherits and inherited_requirements (as its feature states them), " +
            "touches, attempts (execution attempts: holder, outcome active, superseded, abandoned, complete or closed, branch and PR links; the latest three and attempt_count, " +
            "detail full: all), merged_awaiting_acceptance, acceptance_report and open_messages. A feature carries its documents, item counts, goals, requirements " +
            "(active or dropped with the decision, covered_by: the complete items inheriting it), acceptance_tests, and the derived status: complete only when it has " +
            "items, all are complete or closed, one at least is complete and every active requirement is covered (completion lists what is missing). " +
            "A project carries its goals, repositories and item counts. Every object carries version, the expected_version of update_work." + Errors,
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
            "record a handoff, and claim, renew or release it (claims are advisory and do not stop other writes). Complete is set only here, by the agent or person " +
            "who judges the work done, optionally with acceptance_report (summary and the state and evidence of named criteria; not validated); a merge only flags " +
            "merged_awaiting_acceptance. A feature takes goals, requirements (an active one left out must be in drop_requirements with a decision) and " +
            "acceptance_tests; an item takes goal, acceptance_criteria (1 to 3), inherits (requirements of its feature) and touches. With session_id a claim is the session's and lasts " +
            "while the session is live (leave marker and ttl_seconds out); a retried claim the session already holds answers it. A session whose claim on the item " +
            "lapsed is refused claim_lost until it reclaims the unclaimed item (claim) or lets go of it (release_claim). Answers the object as it is after the write, " +
            "with its new version." + Sessions + Errors,
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
            "this session holds with their latest handoff, the other live sessions of the project and what they hold, and one suggested next item (recoverable " +
            "first); empty sections are left out and truncated names sections the budget shortened." + Errors,
            ReadOnly: false, Idempotent: false),
        new(EndSession, "End Session", typeof(EndSessionRequest),
            "End a session: record the handoff (done, decisions, open issues, corrections, next action, branch, commit) on item, or on the one item the session " +
            "holds, and release every claim of the session. An in-progress item it released is then recoverable by the next session, with the handoff. Answers " +
            "the ended session, the released items and the handoff. Ending an ended session changes nothing and answers it." + Errors,
            ReadOnly: false, Idempotent: true),
        new(GetBriefing, "Get Briefing", typeof(GetBriefingRequest),
            "Fetch the session's briefing again (for example after context compaction), optionally including one item (focus) with its latest handoff. Same " +
            "sections and budget rules as start_session. Renews the session's heartbeat." + Errors,
            ReadOnly: false, Idempotent: true),
        new(SyncPlan, "Sync Plan", typeof(SyncPlanRequest),
            "Re-plan a feature in two steps. 1) feature + plan (the revised plan: feature title, description, goals, requirements E1..En, drop_requirements " +
            "with decisions, acceptance_tests, and every item it should hold by ref) answers a preview and changes nothing: items added, edited (field changes), " +
            "closed (items of the feature left out, unless complete or closed), dependency changes, requirements added/changed/dropped, pivots (edited or closed " +
            "items that are in progress or claimed, with their holder) and warnings (items left blocked by a closed prerequisite), plus preview_token. Items " +
            "match by ref (create_work plan refs are kept) or by item key; a field left out of an existing item is unchanged. 2) confirm with that " +
            "preview_token applies it all-or-nothing, only while the feature, its requirements, every item it covers and their dependencies are unchanged since " +
            "the preview; otherwise it is refused conflict and nothing changes (ask for a new preview). Confirming twice answers the first apply (repeated true). " +
            "A pivot supersedes the item's current attempt (evidence from it moves no status), keeps an edited item's claim under a new attempt, ends a closed " +
            "item's claim, comments on the item and gives the holder a changed_by_replan notice on its next work-tool response. A re-plan never completes an " +
            "item, and an active requirement is never dropped silently. A plan holds at most 200 items and 500 dependencies; previews expire after an hour." +
            Sessions + Errors,
            ReadOnly: false, Idempotent: true)
    ];

    public static WorkToolDefinition? Find(string name) => All.FirstOrDefault(tool => tool.Name == name);
}
