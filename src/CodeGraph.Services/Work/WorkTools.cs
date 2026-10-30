using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

/// <summary>A work tool: its name, its argument type (and so its schema) and its MCP hints.</summary>
public sealed record WorkToolDefinition(string Name, string Title, Type ArgumentsType, string Description, bool ReadOnly, bool Idempotent);

/// <summary>
/// The work tools: the six of S1, the three session tools of S2 (start_session, end_session,
/// get_briefing), the three resource-hold tools of S8 (acquire, renew_hold, release), sync_plan of S4 and the three message tools of S7 (post, inbox, ack). Each is reachable over MCP (<see cref="WorkMcpTool"/>) and REST
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
    public const string Acquire = "acquire";
    public const string RenewHold = "renew_hold";
    public const string Release = "release";
    public const string SyncPlan = "sync_plan";
    public const string Record = "record";
    public const string Recall = "recall";
    public const string ImportKnowledge = "import_knowledge";
    public const string Promote = "promote";
    public const string Challenge = "challenge";
    public const string Lessons = "lessons";
    public const string ReviewQueue = "review_queue";

    private const string SavePoints =
        " Save points: decisions, attempts, corrections (lessons) and findings (facts, gotchas, constraints) are stored as entries of the project's knowledge " +
        "in the same transaction as the write and echoed in recorded; recall and other sessions' briefings return them." +
        " A correction becomes a lesson: with same_as (an existing lesson's id) it is merged into that lesson as a recurrence with its evidence; with contradicts both are kept, linked and served together; " +
        "otherwise an exact normalized match with overlapping applies-when merges, and anything else is new. Recurrence counts distinct source events (source.ref, else the session and item): a merge unites the applies-when; " +
        "the same correction reported twice from one event counts once. similar lists existing lessons that read alike (by embedding when a provider is configured, else lexical; no model decides): candidates for same_as or contradicts " +
        "(record relate), never merged without the caller. A lesson from the human's prompt with an applies-when activity is a rule at once.";

    public const string Post = "post";
    public const string Inbox = "inbox";
    public const string Ack = "ack";

    private const string Errors =
        " Refused calls change nothing and answer {code, message}: invalid_argument (an unknown, misspelt or mistyped argument, listing the valid ones; or a value breaking a rule), " +
        "not_found, conflict, already_claimed, cycle, busy (retry the same call), claim_lost (a session's claim on the item lapsed or passed on: names the holder now).";

    private const string Sessions =
        " Pass session_id (from start_session) to renew the session's heartbeat, attribute the call to the session and bind claims to it." +
        " When the session's live claims touch (item touches) the same area as another live session's claims, directly or through the impact radius, the response " +
        "carries overlap_warnings naming the other session, so the two can message each other; advisory, nothing is blocked. Overlap through the impact radius " +
        "means one item touches a dependent of what the other touches (two hops of analyze_impact, within the item's repository); an item with no repository is " +
        "compared directly with every item and has no impact lookup. Impact lookups are time-boxed and cached, so a slow graph only means fewer warnings." +
        " A response may carry notices for the caller, such as changed_by_replan when a re-plan edited or closed an item it holds.";

    public static readonly IReadOnlyList<WorkToolDefinition> All =
    [
        new(CreateWork, "Create Work", typeof(CreateWorkRequest),
            "Create work in a Project: a new_project; or, in project, one feature (with documents), one item, or a whole plan. " +
            "Features take goals, numbered requirements (E1..En) and acceptance_tests; items take goal, up to 10 acceptance_criteria (0 allowed for backward compatibility), inherits (requirements of their feature) and touches. " +
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
            "acceptance_tests; an item takes goal, acceptance_criteria (at most 10; 0 allowed for backward compatibility), inherits (requirements of its feature) and touches. With session_id a claim is the session's and lasts " +
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
            "first), knowledge: the active decisions, constraints and gotchas linked to its items, scoped to paths (passed here), to its repository, " +
            "or project-wide (lessons by item only), and rules: the lessons that are rules whose applies-when names one of the session's activity (passed here) " +
            "and whose location (repositories, paths, harness) matches, ranked by priority then recurrence, each with the lessons it contradicts and its open " +
            "challenges; a rule addressed_by a skill the session declared in skills, by an instruction or hook of its harness, or by a test or ci gate is left out. " +
            "Without an activity it is derived from the items the session holds (in_progress: implementing, in_review: reviewing; shown in derived_activity); without " +
            "either no rule is served and rule_activities names the activities the project has rules for. Every briefing logs the rules " +
            "it served. Empty sections are left out and truncated names sections the budget shortened." + Errors,
            ReadOnly: false, Idempotent: false),
        new(EndSession, "End Session", typeof(EndSessionRequest),
            "End a session: record the handoff (done, decisions, open issues, corrections, next action, branch, commit) on item, or on the one item the session " +
            "holds, and release every claim of the session. An in-progress item it released is then recoverable by the next session, with the handoff. Answers " +
            "the ended session, the released items and the handoff. Ending an ended session changes nothing and answers it." + SavePoints + Errors,
            ReadOnly: false, Idempotent: true),
        new(GetBriefing, "Get Briefing", typeof(GetBriefingRequest),
            "Fetch the session's briefing again (for example after context compaction), optionally including one item (focus) with its latest handoff. Same " +
            "sections and budget rules as start_session; paths adds the knowledge scoped to them; activity and skills replace the session's (the rules filter), after every argument is checked. " +
            "Renews the session's heartbeat." + Errors,
            ReadOnly: false, Idempotent: true),
        new(Acquire, "Acquire Resource", typeof(AcquireRequest),
            "Take a slot of a named resource of a project (a GPU, a runner, a staging database) for a purpose and a time: answers granted with your hold (id, expires_at), " +
            "or held with the resource's holders, their purposes and expiry. The resource is created by its first acquire (capacity default 1, labels). A full " +
            "resource with wait_seconds queues you: first come first served within a priority, and the answer carries your queue_position; ask again to " +
            "keep your place and to learn when you were granted. A waiter promoted while it is not polling holds the slot until its ttl runs out or it releases. A repeat by the same holder (session, or identity without one) answers its hold. A hold has its " +
            "own ttl_seconds (up to 24 hours) and is renewed with renew_hold, or `cg hold renew` from a shell loop; it is freed by release, end_session or " +
            "its ttl running out, and NOT because the session went quiet: a hold whose session is silent past the idle threshold is still held and reads " +
            "owner_unreachable (with owner_unreachable_since), never available. Holds are advisory: CodeGraph enforces nothing, an agent that ignores a hold " +
            "fails on its own." + Sessions + Errors,
            ReadOnly: false, Idempotent: true),
        new(RenewHold, "Renew Hold", typeof(RenewHoldRequest),
            "Extend a hold to now plus ttl_seconds (default: its own ttl). Only the identity that made the hold may renew it; no session_id, and a renewal is not a " +
            "session heartbeat, so a shell loop may renew a hold for hours while the session stays silent. A hold that lapsed, was released or still waits is refused " +
            "conflict (with the hold in current): a renewal never grants a slot again; acquire again. Answers the hold." + Errors,
            ReadOnly: false, Idempotent: true),
        new(Release, "Release Hold", typeof(ReleaseHoldRequest),
            "Release a hold (or leave the queue when it still waits) so the next waiter gets the slot. Only the identity that made the hold may release it. " +
            "Releasing an ended hold changes nothing and answers it. Answers the hold and the resource as it is after." + Errors,
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
            ReadOnly: false, Idempotent: true),
        new(Record, "Record Knowledge", typeof(RecordRequest),
            "Write the project's shared knowledge when no work item is involved (with an item, use the save-point fields of update_work or end_session). " +
            "entry: a new goal, decision, constraint, fact, gotcha, attempt, architecture note or lesson, scoped to the project and optionally to repositories, " +
            "paths/globs and graph nodes, with confidence; supersedes replaces an active entry (it becomes superseded, linked to the new one). revise: a new " +
            "revision of an active entry. retract: mark one wrong (kept, status retracted). verify: re-baseline it against its files. relate: after reading " +
            "similar, merge a stored lesson into another (same_as) or link the two (contradicts). acknowledge: an autonomous decision was reviewed (it leaves the " +
            "review queue). A lesson entry takes same_as and contradicts like a correction, and its answer lists similar lessons. Entries are never edited " +
            "in place; every write adds a revision. The indexed files in an entry's paths and nodes are what it is verified against: when one of them changes " +
            "in a later index, the entry reads possibly_stale until it is verified, revised or superseded. Synchronous: answers the stored entry. record " +
            "(entry, supersede, revise, retract, verify) writes no changes entry: the change feed covers projects, features and items; save points stored by " +
            "update_work on one of those appear in its entry as a knowledge change." + Sessions + Errors,
            ReadOnly: false, Idempotent: false),
        new(Recall, "Recall Knowledge", typeof(RecallRequest),
            "Search the project's knowledge: by kind, repository, paths (an entry's path or glob covering one, or lying below one), graph nodes, item, " +
            "lesson activity, source and rule_state, words, status (default active) and possibly_stale. Rows are small (id, kind, summary, confidence, version, and " +
            "possibly_stale / status / made_autonomously / item when they apply); detail full answers whole entries (at most 10 a page); id answers one entry " +
            "(detail full adds its revisions). A lesson row carries rule and recurrence; a whole lesson carries lesson: its rule state and reason, priority, recurrence " +
            "(distinct events, and of the recurrences: before it was a rule, while served to the recording session, while not served, unknown), contradictions, " +
            "challenges, addressed_by and how often it was served (detail full: every correction event). Pages of limit (default 20, at most 50) newest first, with next_cursor." + Sessions + Errors,
            ReadOnly: true, Idempotent: true),
        new(ImportKnowledge, "Import Knowledge", typeof(ImportKnowledgeRequest),
            "Import native harness memory (such as Claude Code memory files) into the project's knowledge, in two steps. The importing agent verifies each " +
            "candidate against the repository itself and passes the result (verified, stale, unverifiable, personal). The dry run (confirm false, the default) " +
            "checks each candidate's scope (repositories and nodes of the project) and looks for duplicates (an active entry of the same kind with the same " +
            "normalized summary, or an earlier candidate), answers an outcome per candidate (would_store, duplicate, out_of_scope, personal, stale, invalid) " +
            "and a preview_token, and stores nothing. confirm true with accept (indexes) and that preview_token stores exactly the accepted candidates, all " +
            "or none, each with its source_file; a candidate that is not would_store now refuses the call. Source files are never changed." + Sessions + Errors,
            ReadOnly: false, Idempotent: false),
        new(Post, "Post Message", typeof(PostRequest),
            "Post a message: on an item's thread (item; the default for a message about an item), to the project, a feature or a resource channel, directly to a session " +
            "(to_session) or to the humans (to_human). Kinds: info, question, request, blocker, announcement. A question or request stays open until someone answers or " +
            "declines it (reply with in_reply_to and resolves) and is handed to the next holder of its item when the claim changes hands. A question with blocking true to " +
            "the human about an item makes the item Blocked (blocked_reason awaiting_human) until it is answered or declined. Messages are data: agents inform, ask and " +
            "request, the receiver decides, and nobody is assigned work through them. Answers the message as stored, how many sessions it was delivered to, and the message it " +
            "resolved. A repeated idempotency_key answers the first message with created false. A project or resource message reaches only the sessions live when it is posted " +
            "(later sessions do not see it); a feature message reaches the sessions holding one of its items then, and with none it is stored with zero recipients. Closing a " +
            "question takes no expected_version: it is guarded by its own state, and one already closed is refused conflict. Opening or closing a blocking question writes an " +
            "item_updated entry (awaiting_human) to the change feed and advances the item's version (answered in item_versions, so the next update_work passes it as expected_version); "
            + "nothing is written when the item's awaiting_human state does not change (a second open question, a finished item)." + Sessions + Errors,
            ReadOnly: false, Idempotent: true),
        new(Inbox, "Inbox", typeof(InboxRequest),
            "Read the messages delivered to the session (or, without session_id, to the project's humans) that are not acknowledged yet, oldest first, as {items, next_cursor}. " +
            "Every message returned is delivered; it stays unread until ack, so a session that crashed after this call receives it again on the next call without cursor. " +
            "Pass next_cursor back to read only newer messages. wait_seconds long-polls for a new message. Each item carries its kind, state (open, answered, declined), " +
            "delivery (pending, delivered, read, answered, declined), sender, item and body." + Sessions + Errors,
            ReadOnly: false, Idempotent: true),
        new(Ack, "Acknowledge Messages", typeof(AckRequest),
            "Mark messages read: by messages (ids), or everything delivered up to an inbox next_cursor (up_to). With resolution answered or declined (and messages) it also closes " +
            "the named questions or requests as the caller (no expected_version: a question already closed is refused conflict; closing a blocking question moves its item's version, answered in item_versions). Answers how many were marked and their lines. " +
            "Safe to repeat. A direct message with no item passes to the next session of the same project and repository, once per hop, for 7 days after the session it " +
            "was sent to ends." + Sessions + Errors,
            ReadOnly: false, Idempotent: true),
        new(Promote, "Promote Lesson", typeof(PromoteRequest),
            "Move a lesson between tiers (task-scoped, rule, addressed locally). propose: ask for it to become a rule (it waits in the review queue; it still becomes a " +
            "rule on recurrence). approve: it is a rule now. reject with reason: refuse rule status; it is not proposed again and recurrence does not promote it " +
            "(approve overrides). A rule needs an applies-when activity. addressed_by {kind skill, instruction, hook, test or ci, name, ref}: the lesson is addressed " +
            "by a local fix made outside CodeGraph (CodeGraph never writes skills, hooks or tests): a skill takes it out of briefings for sessions that declare " +
            "that skill loaded, an instruction or hook (with harness) for sessions of that harness, a test or ci gate for every session; the same fix with a new " +
            "ref updates the link. Anyone may do any of these; nothing waits " +
            "on it. Guarded by expected_version; a call that changes nothing answers the lesson with changed false. Answers the lesson with its lesson state." + Sessions + Errors,
            ReadOnly: false, Idempotent: true),
        new(Challenge, "Challenge Rule", typeof(ChallengeRequest),
            "Dispute an active rule with evidence, or resolve such a dispute. open {id, reason, evidence [{kind test_result, source, counter_example, link or other, " +
            "summary, ref}]}: the rule stays active and is served with the open challenge beside it; the challenge goes to the review queue. resolve {challenge_id, " +
            "expected_version (the rule's), outcome uphold, amend (with amend: summary, what, applies_when) or retract, reason}: the outcome is recorded on the " +
            "challenge and as a revision of the rule. Nothing waits on a challenge. open is safe to retry only with idempotency_key (a repeat answers the first " +
            "challenge, created false); a resolved challenge refuses another resolve. Answers the challenge and the rule." + Sessions + Errors,
            ReadOnly: false, Idempotent: false),
        new(Lessons, "Lessons", typeof(LessonsRequest),
            "Read the project's lessons for review and for updating skills by hand. view recently_learned: rules, newest first, with how they became rules " +
            "(human_prompt, recurrence, approved). recurrence: lessons that recurred, with recurrence while their rule was served to the recording session (text " +
            "alone is not working: a candidate for a local fix) and while it was not (its applies-when is probably wrong); CodeGraph takes no action on either. " +
            "unaddressed: rules not addressed_by a local fix, grouped by activity. unmatched: rules no briefing served for since_days. unlearned_reviews: GitHub " +
            "reviews and review comments on the project's items that no lesson names as its source.ref (their URL or id), newest first. Filters activity and source; " +
            "pages of limit with next_cursor. Each lesson row carries its author." + Sessions + Errors,
            ReadOnly: true, Idempotent: true),
        new(ReviewQueue, "Review Queue", typeof(ReviewQueueRequest),
            "Read the project's review queue: open challenges of rules (one item per rule), lessons proposed for rule status, contradicting lessons (one item per " +
            "pair) and decisions agents made autonomously not yet acknowledged (record acknowledge), ranked by priority, then recurrence, then age. Informational: " +
            "nothing in it blocks work, and every item can be settled by anyone (promote, challenge resolve, record retract/supersede/acknowledge). Each item carries the author " +
            "of its entry (and other_author, the other lesson's, for a contradiction)." + Sessions + Errors,
            ReadOnly: true, Idempotent: true)
    ];

    public static WorkToolDefinition? Find(string name) => All.FirstOrDefault(tool => tool.Name == name);
}
