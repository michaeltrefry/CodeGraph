# Shortcut Plan Proposal — CodeGraph Agent Coordination

Status: **written to Shortcut** as epic [27864](https://app.shortcut.com/trefry/epic/27864), stories sc-27865 (S1) … sc-27877 (S13), read back and verified (2026-10-29). Source of requirements: `docs/design/agent-coordination.md` (approved 2026-10-29). Team: CodeGraph. Workflow: Standard. All stories start in To Do.

---

## Epic: CodeGraph agent coordination — projects, work, knowledge and messages for autonomous agents

### Objective
Agents in any harness (Claude Code, Codex, anything with MCP or a shell) use CodeGraph as their one place to orient on a project, take and track work from a dependency-ordered plan, record and recall project knowledge and lessons, and coordinate with each other — with near-complete autonomy — while you see everything in the CodeGraph UI. Shortcut work moves into CodeGraph and Shortcut stops being the tracker.

### Requirements (cross-cutting; stories reference these)
- **E1 Strict contract.** Every write returns the resulting state. Unknown, misspelt or mistyped arguments are refused `invalid_argument`, listing the valid ones. Error vocabulary: `not_found`, `conflict`, `already_claimed`, `cycle`, `invalid_argument`, `busy` (retry-safe), `claim_lost`. Creates take idempotency keys; updates take `expected_version`. `snake_case` names only.
- **E2 Project scope.** All new data is scoped to a Project (1..n repositories, `owned` or `dependency`). `repository` means a git repository; `project` means only the new Project.
- **E3 Attribution.** Every write is attributed automatically to its session (harness, model, host) or human. No authorship in bodies.
- **E4 Small by default.** Lists return ids plus one-line summaries; full detail by id or `detail: "full"`; briefings honor a token budget; empty sections are omitted.
- **E5 No hidden model calls.** CodeGraph makes no LLM call to capture, classify, extract or dedupe; embeddings only.
- **E6 Informs, doesn't enforce.** Claims and holds are advisory. No operation is human-only. Nothing in the review queue blocks work. When agents call CodeGraph, what they record and how they judge completion are harness-side (instructions, skills, hooks).
- **E7 Change feed.** Every write to projects, features and items appears exactly once, in commit order, in the pull-based change feed.
- **E8 Surfaces.** Every capability is reachable over MCP and REST; humans can perform every agent action from the UI.
- **E9 Concurrency discipline.** Ported KanbanBoard patterns (conditional versioned updates, named locks, fixed lock order, deadlock retry → `busy`) with tests against real MariaDB.

### Acceptance
- A Claude Code session and a Codex session on one project, with real MariaDB, pass these scenarios: crash recovery with `claim_lost` on return; lost `next_work` response retried without a second claim; a renewed GPU hold surviving 30+ minutes of tool silence as `owner unreachable`; a request to session A in session B's brief after B recovers the item; two concurrent re-plan confirmations with one refused stale and a superseded PR moving nothing; contradictory lessons kept separate and a retried correction counted once; a prompt correction recorded in session A served as a rule to session B.
- All non-archived Shortcut epics and stories (open and done) and the KanbanBoard SceneWorks Code Review 2026-10-27 epic are in CodeGraph, with `sc-NNNN` aliases resolving.
- Agents' local instructions/skills point at CodeGraph instead of Shortcut, and new work is created and tracked only in CodeGraph.

### Slice plan
| # | Story | Type | Depends on |
|---|---|---|---|
| S1 | Work tracking core ported from KanbanBoard | feature | — |
| S2 | Sessions, briefing and `cg` CLI across two harnesses | feature | S1 |
| S3 | `project` → `repository` rename all the way down | chore | S1 |
| S4 | Feature planning, re-planning and completion rules | feature | S2 |
| S5 | Project knowledge store with save points and freshness | feature | S2 |
| S6 | GitHub evidence on work items | feature | S4 |
| S7 | Durable messages with item threads and handover | feature | S2 |
| S8 | Resource holds and overlap warnings | feature | S2 |
| S9 | Lessons, rules and challenges | feature | S5 |
| S10 | UI: work and coordination views | feature | S4, S7, S8 |
| S11 | UI: knowledge, lessons and review queue | feature | S9 |
| S12 | Shortcut, KanbanBoard and native-memory import | chore | S4, S5, S6 |
| S13 | Cutover: scenario acceptance, skills switch, Shortcut read-only | chore | S3, S10, S11, S12 |

S3 depends on S1 only to avoid rebasing the new work code through the rename; it can run alongside S2.

### Scope boundaries
- Writing or editing skills, instructions and hooks is not a CodeGraph capability (E6). Rewriting the local skills for CodeGraph happens in S13, outside this repository.
- KanbanBoard data other than the one epic stays in KanbanBoard.
- The existing memory claim graph is hidden from the default MCP listing (S5), not deleted.
- Per-project access control is out (any valid PAT sees all projects).

---

## Stories

### S1 — Work tracking core ported from KanbanBoard (feature)
CodeGraph has no work tracking. Port KanbanBoard's store and write logic (items, dependencies with cycle check, claims, links, comments, idempotency keys, versioned updates, ordered change feed, plan import) into CodeGraph's MariaDB layer as numbered SQL migrations, with multi-repo Projects, features (epic successor, with documents), per-project speakable keys (`SW-142`), statuses Backlog / Ready / In Progress / In Review / Complete / Closed, and MCP + REST tools `create_work` (single item or plan), `get_work`, `find_work`, `update_work`, `next_work`, `changes`. `next_work` treats only Complete as satisfying a dependency, returns the caller's existing claim unless `another: true`, and takes an idempotency key.

Acceptance:
- An agent imports a plan of features, items and dependencies in one call, drives an item from Ready to Complete over MCP, and every write appears once, in order, in `changes`.
- Retrying `next_work` (same key, or without `another: true`) returns the same claim; concurrent `next_work` calls never give one item to two callers.
- A blocker that is Closed rather than Complete keeps its dependents out of `next_work`.

Inherits: E1, E2, E3, E4, E7, E8, E9
Validation: ported KanbanBoard concurrency/feed/import tests against real MariaDB; new tests for keys, `next_work` retry and Complete-only dependencies; `dotnet test CodeGraph.sln`.
Depends on: none

### S2 — Sessions, briefing and `cg` CLI across two harnesses (feature)
Agents have no identity beyond the PAT username and no first call. Add sessions (harness, model, host, repository, branch, worktree, intent; heartbeat; claims bound to sessions), `start_session` / `end_session` / `get_briefing` with a budgeted briefing, handoffs, recovery of lapsed In Progress items (offered first by `next_work` with branch, PRs, last handoff, unmet AC) and `claim_lost` for a returning session, plus a `cg` CLI (`brief`, `inbox`, `end`) over REST with a 2 s timeout and a one-line unreachable message.

Acceptance:
- A Claude Code session and a Codex session on one project each get a briefing showing the other's live session and claim.
- Killing a session mid-item makes the item recoverable; a second session recovers it with branch, last handoff and unmet AC; the first session's next write gets `claim_lost` naming the new holder.

Inherits: E1, E3, E4, E6, E8
Validation: integration tests for heartbeat lapse, recovery and `claim_lost`; one recorded run with a real Claude Code and a real Codex session.
Depends on: S1

### S3 — `project` → `repository` rename all the way down (chore)
"project" means a repository throughout the existing schema and code (~20 SQL columns over 13 migrations, ~245 C# files, ~18 MCP parameters, 34 Angular files). Rename DB columns, C# identifiers, MCP parameters, REST routes/DTOs and UI labels to `repository`; rename the Neo4j compatibility provider in step or exclude it explicitly; old MCP calls passing `project` for a repository are refused with a message pointing to `repository`.

Acceptance:
- No existing API, MCP tool, UI label, C# identifier or DB column uses `project` to mean a repository.
- An old-style MCP call with `project=<repo>` is refused `invalid_argument` with the explanatory message; the same call with `repository` works.

Inherits: E1, E2
Validation: full `dotnet test CodeGraph.sln` and `ng test --watch=false`; migration applied to a copy of production data; grep audit of remaining `project` uses.
Depends on: S1

### S4 — Feature planning, re-planning and completion rules (feature)
Plans can be created but not revised safely, and completion is undefined. Add feature goals, numbered requirements and acceptance tests; item AC, `inherits`, `touches`; the full self-contained `get_work` brief; `sync_plan` (diff by `ref`, preview token bound to versions, stale confirmation refused, in-progress items editable/closable with the current attempt marked superseded and the holder told via `changed_by_replan`); cancelled-prerequisite blocking; feature completion requiring every requirement covered by a Complete item or explicitly dropped; Complete set only by an agent or human with an optional acceptance report; attempts as first-class records.

Acceptance:
- Two agents confirm previews of one feature concurrently: one applies, the other is refused stale and can regenerate.
- A feature whose items are all Closed is not Complete; dependents of a Closed prerequisite show `cancelled prerequisite` until a re-plan replaces the edge.
- `get_work` returns goal, AC, inherited requirements, touched paths, handoffs, attempts and open messages in one response.

Inherits: E1, E4, E6, E7, E9
Validation: integration tests for preview staleness, superseded attempts, completion rules; plan fixtures at the 200-item limit.
Depends on: S2

### S5 — Project knowledge store with save points and freshness (feature)
Agents have no shared, project-scoped knowledge (the existing memory is per-user, async and unused). Add a synchronous store for goal, decision (with confidence and `made_autonomously`), constraint, fact, gotcha, attempt, architecture and lesson entries, scoped by project, repositories, paths and graph nodes; save-point fields on `update_work` / `end_session` (`decisions`, `attempts`, `corrections`, `findings`, handoff); `record` / `recall`; supersede/retract with revisions; `possibly_stale` flags when files behind an entry change; relevant entries in briefings and briefs; the native-memory import dry-run endpoint (scope and dedup check, nothing stored until confirmed); the existing memory tools hidden from the default MCP listing.

Acceptance:
- A decision recorded in `update_work` by one session is returned by `recall` and in another session's briefing for the same paths, without a separate save call.
- Changing a file an entry was verified against flags the entry `possibly_stale` after the next index.
- The memory tools no longer appear in a default token's `tools/list`; their data is intact.

Inherits: E1, E2, E3, E4, E5, E8
Validation: store and recall tests; freshness test through an index run; entitlement listing test.
Depends on: S2

### S6 — GitHub evidence on work items (feature)
Status depends entirely on agents remembering. Extend the GitHub webhook with push, pull request and review events: link branches/PRs to items by key or `sc-NNNN` alias; move lagging items forward only (In Progress on push, In Review on PR open); record merges and flag `merged_awaiting_acceptance` without completing; PR closed unmerged returns an In Review item to Ready and drafts an attempt; review comments linked as evidence with source and priority; evidence from superseded attempts recorded but moving nothing.

Acceptance:
- Pushing a branch and opening a PR for an item an agent left in Ready moves it to In Review with both links; merging flags it `merged_awaiting_acceptance` and leaves it In Review.
- A PR from a superseded attempt merges and the item's status does not change.

Inherits: E1, E3, E7
Validation: webhook handler tests with recorded GitHub payloads; one live PR on a sandbox repository.
Depends on: S4

### S7 — Durable messages with item threads and handover (feature)
Agents can't talk to each other. Add messages on item threads (default), project/feature/resource channels, direct-to-session and `human`; kinds info / question / request / blocker / announcement; per-recipient delivered / read (`ack`) / answered / declined; `inbox` replay from cursor with long-poll; blocking questions to `human` marking the item Blocked; open requests handed to the item's next holder; unread counts on every work-tool response; SSE to the UI.

Acceptance:
- A request posted to session A about an item appears in session B's brief and inbox after B recovers the item, and stays open until answered.
- A session that crashes after `inbox` delivery but before `ack` receives the message again on replay.

Inherits: E1, E3, E4, E6, E8
Validation: message state and replay tests; long-poll timeout test.
Depends on: S2

### S8 — Resource holds and overlap warnings (feature)
Agents collide on GPUs and on files. Add resources with capacity; `acquire` / `renew_hold` / `release` with their own TTL (≤24 h), FIFO+priority waiters and an `owner unreachable` state when the holder's session is silent but the TTL is live; `cg hold renew`; overlap warnings to both sessions when live claims' `touches` overlap directly or through the graph's impact radius.

Acceptance:
- A hold renewed by `cg hold renew` survives 30+ minutes with no other tool calls and reads `owner unreachable`, never `available`; it frees on release or TTL expiry.
- Two live sessions claiming items whose `touches` overlap through an impact path each get a warning naming the other session.

Inherits: E1, E6, E8
Validation: hold lifecycle tests with a fake clock; overlap test on an indexed fixture repository.
Depends on: S2

### S9 — Lessons, rules and challenges (feature)
Corrections die with the session. Add lessons with applies-when (activity plus optional location), source and priority; similarity returning merge candidates only, with `same_as` / `contradicts` decided by the recording agent and exact normalized matches merged; recurrence counted by distinct source event; your-prompt lessons active as rules immediately; rules served in briefings when applies-when matches, with the served-rule log; `challenge` with evidence (rule stays active, shown with the challenge); `promote` and `addressed_by` links to local skills/hooks/tests; no time decay.

Acceptance:
- "Always X" and "never X" with the same scope stay two rules, both served with the contradiction shown; a correction reported twice from one event counts once.
- A correction recorded from a prompt in session A is served as a rule to session B when B's activity matches.

Inherits: E1, E3, E4, E5, E6
Validation: dedup/recurrence/contradiction tests; briefing rule-selection tests.
Depends on: S5

### S10 — UI: work and coordination views (feature)
You can't see agent work in CodeGraph. Add Angular pages: Now (live sessions, holds and waiters, items blocked on you), Project (goals, member repositories, features with progress, recent activity), feature views (board with drag-to-move, dependency graph with critical path and parallel sets, progress), item detail (AC, timeline of status, commits, PRs, reviews, messages, handoffs, attempts) and the message board, all live-updating.

Acceptance:
- Every agent action from S1–S8 is visible in these pages with its actor, and you can perform each of them from the UI.
- The dependency graph view shows blocked, cancelled-prerequisite and parallel items for a 50-item feature.

Inherits: E3, E8
Validation: `ng test --watch=false`; manual pass in light and dark mode.
Depends on: S4, S7, S8

### S11 — UI: knowledge, lessons and review queue (feature)
Knowledge and lessons have no human view. Add Angular pages: Knowledge (browse/search, stale flags, supersession chains), Lessons (by source, priority, recurrence, tier; served-vs-recurred; process lessons not addressed by a skill), "recently learned", and the review queue (autonomous decisions, challenges, proposed rules) with one-click approve / reject-with-reason / amend.

Acceptance:
- You can retract a recently learned rule and resolve a challenge from the UI, and the next briefing reflects it.
- The review queue lists autonomous decisions and open challenges without blocking any work.

Inherits: E6, E8
Validation: `ng test --watch=false`; manual pass in light and dark mode.
Depends on: S9

### S12 — Shortcut, KanbanBoard and native-memory import (chore)
Most work lives in Shortcut (~399 epics, ~7,300 stories, open and done). Build a re-runnable import job: dry-run mapping report (team → project table, counts, items without inferable repository, unmapped relations, attachment volume); import after your approval of the team → project table; epics as features with requirements flagged unreconciled and a reconcile task for open features; checklists as structured AC with state; sub-tasks as dependent items; relations, comments with original authors and times, links and downloaded attachments; `sc-NNNN` aliases; delta syncs by `updated_since`; plus the KanbanBoard SceneWorks Code Review 2026-10-27 epic; plus the native-memory import run (agent verifies Claude/Codex memory candidates, you accept/edit/drop them, then they're stored through S5's dry-run endpoint). Shortcut, KanbanBoard and memory files are never written.

Acceptance:
- The dry run produces the mapping report and writes nothing; the approved import brings in every non-archived epic and story with counts matching the report.
- `get_work("sc-NNNN")` resolves for imported stories, and a second run after a change in Shortcut updates that item without duplicating anything.

Inherits: E1, E2, E3, E7
Validation: mapping tests on recorded Shortcut payloads; one full dry run and one import against a staging copy; count reconciliation.
Depends on: S4, S5, S6

### S13 — Cutover: scenario acceptance, skills switch, Shortcut read-only (chore)
Switch from Shortcut to CodeGraph. Run the epic acceptance scenarios with a Claude Code and a Codex session against real MariaDB; rewrite the local skills/instructions (global CLAUDE.md workflow, `shortcut-plan`, `shortcut-story`, `shortcut-epic`, `sceneworks-epic`) for CodeGraph outside this repository; run a final Shortcut delta sync; mark Shortcut read-only; read back the tracker state.

Acceptance:
- All seven epic acceptance scenarios pass and their evidence is attached to this story.
- After the final delta sync, new work is created and tracked only in CodeGraph by the rewritten skills, and Shortcut receives no further writes.

Inherits: E1–E9
Validation: the scenario runs; reuse story-level evidence for everything else.
Depends on: S3, S10, S11, S12
