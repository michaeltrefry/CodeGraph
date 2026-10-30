# CodeGraph Agent Coordination — Requirements Draft

Status: **draft, revised after Codex review; all questions decided** (2026-10-29). Awaiting approval. Not yet sliced into work items.

Goal: near-complete autonomy — you have an idea, a plan is made, and agents carry it out with as little input from you as possible. CodeGraph is the one place those agents go to (1) know a project, (2) find, do and track work, and (3) coordinate with each other — replacing Shortcut and the separate Kanban service — while giving a human a clear view of everything. CodeGraph becomes owned, critical infrastructure on purpose: the point is to stop depending on third-party trackers.

---

## 1. Why agents under-use CodeGraph today

Facts from the code (high confidence):

- The MCP surface is ~50 tools (graph, memory, conventions, hub, overlay) plus consolidated duplicates. An agent picking tools from a long list picks the ones the task obviously needs and skips the rest.
- Nothing pulls an agent in. No call is the obvious "first thing to do in a session." CodeGraph waits to be asked.
- Memory is keyed per username, with no project, repo or path scope and no expiry. A fact about SceneWorks and a fact about CodeGraph sit in one pool.
- Memory writes are async, with receipts. An agent has to poll to know a write landed, and agents skip that step.
- Identity is the PAT's username only. Two agents running at once are indistinguishable.

Facts from use (the existing memory system):
- It was put in instructions, in skills and behind a hook, and was called, but not consistently.
- The hook often caused problems and confused agents.
- Agents could not answer **"when should I save a memory?"**, so saving became a manual request from you.
- Claude and Codex then shipped their own memory systems, which made CodeGraph's look pointless. Each of those is private to its harness: Codex cannot read Claude's memory and Claude cannot read Codex's.

What this design takes from that (medium-high confidence):
1. **Never ask the agent "when to save."** Knowledge is captured as fields on calls the agent already makes for work tracking — planning a feature, updating an item, handing off — not through a separate "save memory" decision (§5.8).
2. **CodeGraph serves information; it does not drive the agent.** Hooks, instructions and skills that decide when an agent calls CodeGraph are local to each harness (P13, §9).
3. **Don't compete with native memory.** Harness memory keeps personal and harness-specific preferences. CodeGraph holds what every agent on a project needs to share, whatever harness it runs in.

My read on the cause (medium confidence): agents use a tool when **(a)** the harness puts it in front of them at the right moment and **(b)** one call returns something they could not get by reading the repo. CodeGraph does (b) for code structure but not for project state, and does not do (a) at all.

---

## 2. How an agent actually works — the constraints the design has to fit

1. **Starts cold every session.** It knows nothing beyond what the harness injects and what it reads.
2. **Context is the scarce resource.** Every token a tool returns pushes out working memory. A 20 KB response is a cost, not a gift.
3. **Loses state mid-task.** Compaction, crashes, rate limits and session ends drop context without warning. Whatever was not written down is gone.
4. **Forgets procedure.** "Remember to update the tracker / read back the write / post a closeout" fails often. A rule that depends on agent discipline will rot (the same principle as CodeGraph's "self-maintaining" rule).
5. **Trusts what it reads.** A stale fact gets acted on as if it were current. A confident wrong note is worse than no note.
6. **Repeats dead ends.** Without a record of what failed, the next agent tries the same thing.
7. **Cannot see other agents.** It does not know someone else is editing the same files, holding the GPU, or already halfway through the same story.
8. **Different harnesses, different powers.** Claude Code has hooks; Codex, Cursor and others vary. MCP plus a plain HTTP/CLI fallback is the only common surface.

---

## 3. Design principles

| # | Principle | What it means in practice |
|---|---|---|
| P1 | **One call to get oriented** | `start_session` returns a budgeted briefing: goals, relevant decisions, your work, inbox, who else is active, resource holds. |
| P2 | **No separate "remember to save" step** | Knowledge is captured inside work-tracking calls the agent already makes. A local session-start hook can inject the briefing. |
| P3 | **Agents drive state; evidence backs it up** | Agents and orchestrators set status themselves. GitHub events attach evidence and move an item forward only when it lags behind what the evidence shows. |
| P4 | **Loud failure is the friendly failure** | Every write returns the resulting state, so no read-back. Unknown or misspelled parameters are rejected with a message naming the parameter and the valid ones. Creates take idempotency keys. |
| P5 | **Small by default, full on request** | Lists return ids plus one-line summaries. `detail: "full"` or a get-by-id returns the rest. Every briefing has a token budget. |
| P6 | **Knowledge carries its freshness** | Facts and decisions record the commit and the files they were verified against. CodeGraph knows when those files changed and flags the note as possibly stale. |
| P7 | **Negative knowledge is first-class** | "Tried X, failed because Y" is a record type, attached to work items and code paths, and surfaced before an agent repeats it. |
| P8 | **Coordination is informational** | Work claims and resource holds tell other agents what is in use; they expire unless renewed by a live session. CodeGraph does not enforce them. An agent that ignores a hold and collides with it fails on its own. |
| P9 | **Few strict tools** | ~16 new tools with strict schemas, not 40. Clear verbs beat one tool with a polymorphic `operation` field (loose schemas are where silent-ignore bugs come from). |
| P10 | **Humans see the same data** | Every agent action is visible in the UI with who/what/when. Humans can do everything agents can, and answer questions agents escalate. |
| P11 | **Corrections compound** | A correction that should survive the session becomes a lesson; recurring lessons become rules, and some get addressed locally by skills, hooks or tests (§5.7). Your own corrections rank highest but are not law: an agent with evidence can challenge one. Save the smallest thing that changes future behavior. |
| P12 | **No hidden LLM calls** | Capture and dedup use only data the agent already sends plus embeddings. CodeGraph makes no per-prompt or per-event model call. |
| P13 | **CodeGraph informs; the harness enforces** | CodeGraph stores, relates and serves information, and reports what it observes. When and whether an agent saves, reads, challenges, or obeys anything is managed locally through instructions, skills and hooks. |
| P14 | **Built for autonomy** | No operation is human-only, and nothing waits on you by default. An agent facing a judgment call decides, records the decision with a confidence level, and continues; you review later. Blocking on you is a separate, last-resort state. |

---

## 4. The agent loop (what a session looks like)

```
session start (hook runs `cg brief` → injected into context)
  │  briefing: project goals · decisions/rules touching this area · my claimed item + last handoff
  │            · inbox · other live sessions in this project · GPU holds · suggested next item
  ▼
next_work(project)            → atomically picks the best Ready, unblocked, unclaimed item
  │                             and claims it (lease tied to this session)
  ▼
work … recall(...) when unsure → scoped search of decisions/facts/failed attempts
  │    record(...) when learning something durable (decision, gotcha, failed attempt)
  │    acquire(resource:"gpu:4090-box") before a GPU job → granted, or held by X until T
  │    post(...) to coordinate on a conflict; inbox(...) before starting new work or while waiting
  │    you correct it → update_work(..., corrections: [...]) in the same call it already makes
  ▼
update_work(status)           → the agent moves its own item: In Progress, In Review, Complete
  │                             (GitHub events attach branch/PR/review/merge evidence and fill in
  │                              a status the agent forgot; never move an item backward)
judgment call                 → decide, record decision + confidence, continue (reviewed later)
  ▼
end_session(handoff) : done / decisions / open issues / corrections / next action
                       claims and holds released; handoff shown to the next agent
```

What the agent has to remember by itself: call `start_session` (if no hook), `next_work`, `update_work`/`end_session` with their fields filled, `acquire` before GPU work. If it forgets a status move, GitHub evidence fills it in.

---

## 5. Domain model

### 5.1 Projects and repositories

**Repository** means exactly a git repository. **Project** is the higher-level thing you work on, and it spans repositories.

- **Project**: key (`SW`), name, goals, member repositories, and the work and knowledge for all of them.
- **Member repository** has a role:
  - `owned` — you manage it; work items, branches and PRs happen here (SceneWorks, inference, mlx-rs).
  - `dependency` — managed elsewhere (upstream crates, third-party libraries); indexed for context, no work items.
- A repository can be `owned` by one project and a `dependency` of several.
- Repositories not assigned to a project keep working as today.

**Relabel.** Today "project" means a repository nearly everywhere, from when it meant a C# project: ~20 SQL columns across 13 migrations, ~245 C# files, ~18 MCP tool parameters, 34 Angular files.
- **Renamed all the way down**: DB columns, C# identifiers, MCP parameters, REST routes/DTOs and UI labels say `repository` when they mean a repository. Agents read the code too; a false word left in the schema misleads every future agent.
- An old `project` parameter that meant a repository is **rejected** with a message ("`project` now means a multi-repo project; pass `repository`"), not silently reinterpreted. Silent reinterpretation would hand old callers the wrong scope.
- The Neo4j compatibility provider is renamed in step or explicitly excluded; it is not left half-renamed.

### 5.2 Work

**Base: port KanbanBoard into CodeGraph** (D19). Its store, items, dependencies, claims, plan import and change feed move into CodeGraph and get extended as below; the separate service is then retired. The details of the port depend on a read of KanbanBoard's domain code, not done yet.

**The model is a dependency graph with a ready queue, not a board.** Agents never need "which column is this card in"; they need "what can I do now, what do I need to know to do it, and what can run in parallel". The board is one human view of the same data (§8).

**Entities**

| Entity | Purpose | Fields |
|---|---|---|
| **Feature** (replaces epic) | A goal-sized body of work in a project, possibly spanning repos | goals · numbered requirements (`E1..En`) · acceptance tests · documents · status derived from items |
| **Item** — type `story` / `task` / `bug` | One executable slice, normally in one owned repository (optional for non-code work such as research spikes, and for imported items whose repository can't be inferred) | title · goal · 1–10 acceptance criteria · `inherits: [E2, E5]` · `touches` (expected paths/symbols) · `blocked_by` · priority · labels |
| **Handoff** | Structured state of an item when a session stops | done · decisions (links to `decision` entries) · open issues · corrections · next action · branch/commit |
| **Attempt** | A failed or abandoned approach | what was tried · why it failed · commit/branch · related paths |
| **Comment** | Free-form discussion on an item or feature | author (session or human) · body · thread |

- Ids are short and speakable: `SW-142`, `SW-F12`. Agents and humans use the same id; branch names embed it.
- No sprints, iterations or story points. Priority order within Ready is enough.

**Statuses**
- `Backlog`, `Ready`, `In Progress`, `In Review` (PR open), `Complete`, `Closed` (won't do).
- **Complete is the agent's call** (D27, revises D7). A merge is recorded as evidence and flags the item `merged_awaiting_acceptance`; it does not complete the item. The agent marks it Complete when it judges the work done, and can attach an acceptance report (AC met, with evidence) as part of that `update_work`. CodeGraph does not check the report against the AC; what counts as done is a skill norm.
- **Blocked is derived, not a column**: a `blocked_by` item that is not Complete, or an open question that blocks on you (last resort, P14).
- **Only Complete satisfies a dependency.** A Closed prerequisite does not: its dependents show `blocked: cancelled prerequisite` until a re-plan removes or replaces that edge. Cancelling "implement authentication" must not release "expose authenticated API".
- **Ready is computed for the queue**: status Ready, every blocker Complete, no live claim.
- A feature is `Complete` when every item is Complete or Closed **and** every requirement `E1..En` is covered by at least one Complete item (via `inherits`) or explicitly dropped by a re-plan with a recorded decision. Closing every item does not complete a feature.
- **Imported blocked items:** an item a source marks blocked keeps its status and gets `blocked_by` edges when the source has them, so it is skipped by `next_work` until its blockers finish and then becomes available without anyone touching it. One marked blocked with no edges (an external reason) is imported as `Backlog` with a comment carrying the reason, so no agent picks it up blindly.
- Agents and orchestrators can make every transition. Nothing is human-only.

**Discrete items are the unit of work.** An agent gets one item from `next_work`, works it, and finishes it. Each item has its own status, claim, AC, history and handoffs.

**The item is a self-contained brief.** `get_work` returns everything needed to start, in one response: goal and AC, the feature requirements it inherits, linked decisions and rules for its activity and paths, files and graph nodes it touches, previous handoffs and attempts, open messages about it. Agents lose more time gathering context than choosing work.

**Parallelism comes from the graph.** Items with no dependency path between them can run concurrently; `next_work` and the briefing can show which Ready items are independent of what other live sessions hold.

**Plans go in as a document; items are the source of truth after that.**
- *Planning*: the agent submits a whole plan in one call — feature, requirements, documents, items each with a stable `ref`, and dependency edges. CodeGraph creates the items. All-or-nothing, idempotent, cycle-checked.
- *Re-planning*: the agent submits the revised plan. CodeGraph diffs it by `ref` against the existing items and returns a preview — items added, edited, closed, dependencies changed — plus a **preview token bound to the versions** of every affected item, requirement and dependency. Confirmation passes the token; if anything it covers changed since, the confirmation is refused `conflict` and the agent regenerates the preview. The confirmation is the agent's, not yours.
- *Pivots on work in progress*: a re-plan may edit or close an item that is In Progress or claimed. The current attempt (its claim, branch and PRs) is marked **superseded**. The holder is told on the item thread and by a `changed_by_replan` notice on its next work-tool response. Evidence from a superseded attempt — a late push, a PR merged afterwards — is recorded on the item but never moves its status. If the item was closed, the claim ends and the session's handoff is kept as an attempt.
- The plan document is not stored as the source of truth; the items are. Single items can still be edited directly.

**Change feed.** Every change to work is an entry in a pull-based feed (cursor + long-poll), so an orchestrator or agent can follow a project without webhooks — agent harnesses generally can't receive them.

**Concurrency.** Every update takes `expected_version`; a stale write is refused with the current state so the agent can merge.

### 5.2a KanbanBoard port map

From a read of `~/Repos/KanbanBoard` (2026-10-29). Size: ~5k lines of domain services, ~6k of generated EF migration designers, a 2.5k-line vanilla-JS UI, a separate 1.4k-line MCP host with 38 tools, ~14.7k lines of tests (many against a real MariaDB). The most valuable part is not the entity model, which is simple, but the **concurrency and API discipline**, documented in `docs/mariadb-schema.md` and `docs/mcp-contract.md` and tested.

**Ports as-is (logic and SQL patterns, rewritten to CodeGraph's data-access style)**

| KanbanBoard | Why it's worth keeping |
|---|---|
| Work items, comments, links (`pr` / `branch` / `other`, URL hashed for exact lookup) | Direct match for §5.2 |
| Dependencies with cycle check under a named lock | Exactly the dependency graph; correct under concurrent adds |
| Claims: identity + marker + TTL, one winner, advisory | Matches P8; the session id becomes the marker |
| `version` / `expected_version` via conditional `UPDATE` | Matches R10 |
| Idempotency keys (hashed, per identity and parent, repeat returns the object as it is now) | Matches R7 |
| Plan import: all-or-nothing, cycle-checked, limits (200 items / 20 docs / 500 deps / 2 MB) | The planning half of `create_work` |
| Change feed: ordered by committing under a named lock, cursor never skips, measured and tested | Basis of `changes` (R11a) |
| Strict arguments: unknown/misspelt/mistyped refused `invalid_argument` listing valid args | Already P4/R10 |
| Error codes `not_found` · `conflict` · `already_claimed` · `cycle` · `invalid_argument` · `busy` (retry-safe) | Adopt as the error vocabulary for all new tools |
| Deadlock retry (3 attempts → `busy`) and the documented lock order | Keeps the ported writes correct |
| The concurrency tests that run against real MariaDB | Port with the code; they are the proof the patterns work |

**Changes in the port**

| KanbanBoard | In CodeGraph |
|---|---|
| Project with one `RepositoryUrl` + `DefaultBranch` | Project with many member repositories (`owned` / `dependency`); each item belongs to one owned repository |
| Statuses `Backlog, Ready, InProgress, Blocked, Done, Closed` | `Backlog, Ready, In Progress, In Review, Complete, Closed`; Blocked derived |
| Types `Story, Issue, Task` | `story, task, bug` (Issue → bug) |
| Epic + epic documents | Feature + documents, plus goals, numbered requirements, acceptance tests |
| Item fields | Add goal, AC, `inherits`, `touches`, repository; drop `Estimate` |
| `Order` per status column, renumbered on move | Priority order used by `next_work`; the board's drag-reorder writes it |
| GUID ids only | GUIDs internally, plus per-project sequential keys (`SW-142`, `SW-F12`) — new |
| Claim held by identity + caller-chosen marker | Claim held by a session; lapses on heartbeat loss as well as TTL |
| Change feed covers work items only; projects/epics have no `version` or feed entries | Features and projects get `version` and feed entries too (re-plans change features) |
| `update_epic` / `update_epic_document` replace the whole object, unconditionally | Partial, versioned updates like items |
| Mixed `camelCase` (older) and `snake_case` (newer) argument names | One spelling: `snake_case` |
| Search by `LIKE` on title/description | CodeGraph's existing search/embeddings |
| In-process SSE notifier for the board | Same pattern for the UI; fine while one API instance serves it |
| 38 board-shaped MCP tools | Replaced by the ~16 tools of §6; the ported logic sits behind them |
| `move_work_item` as the main state change | Status set through `update_work`; move kept for the human board |

**Not ported (CodeGraph already has its own)**
- Auth: OIDC, `AppUsers`, PATs with `read`/`full` scope, service identities, admin pages → CodeGraph PATs, entitlements and sessions. KanbanBoard's per-agent service identities map to one PAT per agent identity; the session adds harness/model/host on top.
- The separate MCP host and its internal shared-secret API hop → tools live in `CodeGraph.Api`'s MCP server.
- EF Core migrations on the Oracle `MySql.EntityFrameworkCore` provider and its `GET_LOCK` interceptor → numbered SQL files in `sql/migrations` on MySqlConnector/Pomelo. The SQL lock patterns carry over; the provider workaround does not.
- The vanilla-JS UI → rebuilt in Angular (§8).

**New, nothing to port:** sessions, multi-repo projects, feature requirements/AC/`inherits`/`touches`, item briefs, `sync_plan` diff and pivots, handoffs/attempts, GitHub evidence, knowledge, messages, holds, lessons, overlap warnings, review queue.

**Existing KanbanBoard data:** only the epic **SceneWorks Code Review 2026-10-27** (188 findings from SceneWorks/SceneWorks#3220 plus the follow-up pass, 274 open items) is imported. Everything else stays in KanbanBoard, which is unused otherwise. Mapping for that epic: epic → feature (its description and documents kept); Issue → bug, Task → task; Backlog → Backlog, Ready → Ready, InProgress → In Progress, Done → Complete, Closed → Closed; claims not carried over; comments, links and dependencies carried over. (The epic has no `Blocked` items as of 2026-10-29.)

**Effort share (low-medium confidence):** the port covers roughly the core of slices 2 and 4 (store, dependencies, claims, import, feed) — perhaps a third of the tracker work and 15–20% of the whole epic. It saves most on correctness: the concurrency design is the hard part, and it is already worked out and tested.

### 5.3 Knowledge

A new store, separate from the existing memory claim graph. **Scoped to a project and its repositories, not to a user.** Every agent and person with access to the project reads and writes the same knowledge.

| Kind | Example |
|---|---|
| `goal` | "SceneWorks 1.0: one-click local install on Windows + CUDA" |
| `decision` (ADR) | context · decision · consequences · confidence · status · supersedes · `made_autonomously` (shown in your review queue) |
| `constraint` | "MLX Metal is single-threaded; never run two MLX jobs on the dev Mac" |
| `fact` | "Dev Mac is also a CI runner" |
| `gotcha` | "Shortcut tools ignore unknown params and still report success" |
| `attempt` | "Tried candle flash-attn on sm_86 — segfaults in kernel X; abandoned" |
| `architecture` | Narrative notes; may also be generated from the graph |
| `lesson` | "Distinguish announcement date from availability date" — with applies-when, source and recurrence (§5.7) |

Every entry has:
- **Scope**: project (required), optional repositories, optional paths/globs, optional graph node ids. CodeGraph can then return "decisions that touch the files you are about to edit."
- **Provenance**: author (session or human), harness, model, commit SHA.
- **Freshness**: `verified_at` commit + the file hashes it was based on. When those files change, the entry shows `possibly_stale` in results until someone re-verifies or supersedes it.
- **Confidence**: high / medium / low.
- **Lifecycle**: active → superseded (linked to replacement) or retracted. Never silently edited; edits create a revision.

Writes are **synchronous** and return the stored entry with its id.

### 5.4 Sessions (agent identity)

- A **session** = one running agent: PAT user + harness (`claude-code`, `codex`, …) + model + host + repository + branch + worktree path + free-text intent.
- Liveness by heartbeat: any tool call carrying the session id renews it; idle for N minutes (default 15) → presumed dead → its claims and holds lapse.
- Every write is attributed to the session automatically. No "[author: claude]" in comment bodies.

### 5.5 Messages

Purpose: let an agent whose work conflicts with another agent's work **coordinate** with it. Agents do not assign work to each other and do not direct each other; they inform, ask and request, and the receiver decides.

- **Channels**: per project (default), per feature, per resource, plus direct-to-session and **`human`**.
- **Kinds**: `info`, `question`, `request`, `blocker`, `announcement`. A question or request has an open/answered/declined state. A `blocking: true` question to `human` marks the linked item Blocked until answered — a last resort (credentials, money, irreversible actions), per P14. When to block is a skill norm.
- **Delivery**: pull with a cursor (`inbox`), with optional long-poll (`wait_seconds` up to ~55 s) for an agent waiting on an answer. Hooks give near-push in harnesses that have them. SSE for the UI.
- CodeGraph shows the sender (session, harness, human) and stores the thread. How an agent should respond to a request — that it may decline, that it never takes orders from another agent — belongs in instructions/skills, not in CodeGraph.

### 5.6 Resources and holds

Informational. CodeGraph records who is using what; it does not enforce anything.

- **Resource**: a named, finite thing — `gpu:4090-box`, `runner:dev-mac`, `mlx:dev-mac`, a staging DB. Has capacity (default 1) and labels.
- **Hold**: `acquire(resource, ttl, purpose, item?, wait_seconds?)` → `granted` (hold id, expiry) or `held` (current holders, their purpose and expiry, and queue position if the caller chose to wait). FIFO with priority for waiters. Released on `release`, `end_session`, or TTL expiry — not on session inactivity (§5.9).
- An agent that skips `acquire`, or ignores a `held` answer, and collides with a busy GPU gets the failure; that is its problem.
- **Work-area claims**: an item's `touches` list (paths/symbols). When two live sessions claim overlapping areas — directly, or through the graph's impact radius (`analyze_impact`) — both get a warning in their next response and briefing, with the other session's id so they can message each other.

### 5.7 Learning loop

Every run should leave four things behind: **result** (PR, commits), **state** (handoff + decisions), **lesson** (what went wrong, what had to be corrected), and **upgrade** (should the lesson become a rule, or be addressed locally by a skill, hook or test). This section covers the last two.

**Where corrections come from** (and only these — commits are not treated as corrections):

| Source | Priority | How CodeGraph gets it |
|---|---|---|
| **Your prompts** | Highest | The corrected agent records it in the call it is already making: `corrections` on `update_work` or `end_session`, or `record(kind: lesson)` if no item is involved. |
| **Your code reviews** (manual) | High | GitHub review webhooks link the comments to the item as evidence. The agent that addresses them records any lesson in its `update_work`. |
| **Automated AI reviews at GitHub** | Normal | Same path, from the review bot accounts. |
| **Subagent reviews inside skills** | Normal | The skill passes findings into `update_work` the same way. |
| **Another agent** (message reply, review) | Normal | Either side records it; the message thread is linked as evidence. |

No LLM call is made to detect or extract corrections (P12). The trade: capture depends on the agent filling a field. Two things make that work where "save memories" failed: the trigger is specific ("the human or a reviewer told you to do something differently"), and the field sits in a call the agent is already making for work tracking, so no separate decision is needed. Webhook evidence means a review that was never turned into a lesson is still visible on the item and in the Lessons page as "review comments with no lesson".

**Lessons**
- A `lesson` records: what happened · the correct behavior · **applies when** · source and priority · linked item/PR/message.
- **Applies-when has two dimensions**, because most of your corrections are about process, not code:
  - *activity*: planning, slicing, implementing, testing, reviewing, merging, releasing, tracker updates, coordinating;
  - *location* (optional): repositories, paths, tools, harness.
  A process lesson usually has an activity and no paths ("when slicing a plan, don't restate epic requirements in stories").
- **Applies-when is required to leave task scope.** A lesson with no trigger would be injected everywhere and dilute every briefing.
- **Embeddings find candidates; they never decide.** On `record`/`corrections`, the response lists similar existing lessons. The recording agent decides: `same_as: <id>` (merge as a recurrence), `contradicts: <id>` (both kept and flagged to the review queue), or new. Without a decision, an exact match after normalization is merged; anything else is stored as new. No model call in CodeGraph (P12).
- **Recurrence counts distinct correction events.** Each correction carries its source event (prompt/session id, review comment id, message id); repeats of the same event — retries, a correction reported in both `update_work` and `end_session` — count once.
- **Contradictions are preserved.** Two active rules marked contradictory are both served with the conflict shown, until you or an agent with evidence resolves it.

**Your corrections are active immediately, and challengeable.**
- A lesson from your prompt becomes an active rule at once and appears in a "recently learned" list where you can edit or retract it.
- You can be wrong. An agent that believes a rule is wrong `challenge`s it with evidence (a test result, a source, a counter-example, a link). The rule stays active, but the briefing shows it with the open challenge, and the challenge goes to your review queue. You uphold it, amend it, or retract it; the outcome is recorded on the rule.
- When and how to challenge — present evidence, don't just ignore the rule — is a norm for instructions/skills; CodeGraph provides the mechanism.
- Process lessons are the ones most likely to end up in a skill. The Lessons page shows process lessons not yet `addressed_by` a skill, as input for a manual skill update.

**Tiers**

```
task-scoped lesson ──► rule ──────────────────────► addressed locally
(lives on the item)    (served in briefings when      (skill, instruction, hook, test, CI —
                        applies-when matches)          made in the harness/repo, then marked
                                                       addressed_by on the lesson)
```

| Tier | Where it lives | When |
|---|---|---|
| Task-scoped | The item; gone when the item closes | Default for one-off review findings |
| Rule | `constraint`/`lesson` entry or conventions page, with applies-when | Recurs across items; or comes from your prompt (immediately) |
| Addressed locally | A skill, instruction, hook, test or CI gate — outside CodeGraph | You and an agent decide a lesson needs it; CodeGraph only records the `addressed_by` link |

**Recurrence is information, not a trigger.** Every briefing logs which rules it served, so the Lessons page can show:
- a lesson that recurred while its rule was **not** served → its applies-when or ranking is probably wrong;
- a lesson that recurred while its rule **was** served → text alone is not working; a candidate for a local fix (skill, hook, test).
CodeGraph does not act on either; it shows them.

**Skills are manual.** CodeGraph does not generate or edit skills or instruction files. It supplies the input: `recall(kind: lesson, …)` grouped by applies-when, recurrence and source, so you and an agent can update a skill together. A lesson can then be marked `addressed_by: skill <name> @ <commit>`, which takes it out of briefings for sessions that load that skill.

**Human role: review later, never a gate** (P14). Rule challenges, lessons proposed for rule status, and decisions agents made on their own land in a **review queue**, deduplicated and ranked by priority then recurrence. Nothing in it waits on you: lessons become rules through recurrence, a challenged rule stays active until you get to it, and an agent's decision stands until you overturn it. Approving is one click; rejecting with a reason records why so the same candidate is not re-proposed.

**No time decay.** A rule leaves briefings only when superseded, retracted, or `addressed_by` a local skill. A rule that matches rarely (a release constraint) is still served the next time its applies-when matches — that is what applies-when filtering is for, and it is what keeps the context cost down. The Lessons page shows rules that haven't matched in a long time, for review.

### 5.8 Save points (answering "when do I save?")

The old memory system left the agent to decide when something was worth saving, and it couldn't. Here every kind of knowledge has a fixed save point: a field on a call the agent makes anyway.

| Knowledge | Save point | Field |
|---|---|---|
| Goals, requirements | Creating or editing a feature | `create_work` / `update_work`: `goals`, `requirements` |
| Decision | Planning a feature, or choosing between approaches on an item | `decisions: [{decision, why, alternatives}]` |
| Failed attempt | Abandoning an approach, or a PR closed unmerged | `attempts: [{tried, why_failed}]`; drafted automatically for closed PRs |
| Correction / lesson | Being corrected by you, a reviewer or another agent | `corrections: [{what, correct_behavior, applies_when, source}]` |
| Fact, gotcha, constraint | Discovering something the next agent would otherwise rediscover | `findings: [...]` on `update_work` / `end_session` |
| State | Stopping work on an item | `end_session` / `update_work(release)`: handoff |

- Each field is optional, typed and small. An empty field is fine; a wrong guess is worse than nothing.
- `record` still exists for knowledge with no item (e.g. a project-wide fact), but it is the exception, not the main path.
- The response to each write echoes what was stored, so the agent sees that it landed (no receipts, no polling).
- Field descriptions say what a field means, not when an agent must fill it. When to fill them is a norm for local instructions/skills (P13). The save points just make that norm short to write: "when corrected, put it in `corrections`" instead of "decide when to save a memory."

### 5.9 Failure and concurrency contracts

The normal path is above. These are the contracts for interrupted, concurrent and contradictory cases.

**Abandoned work is recovered, not stranded.**
- An item In Progress whose claim lapsed is **recoverable**. `next_work` offers recoverable items before fresh Ready ones (same filters).
- Recovering returns the existing branch and PRs, the latest handoff (or, if the session died without one, its last `update_work` state), and the acceptance criteria not yet reported as met. The recovering session continues on the same branch unless it records a decision to restart.
- A session whose claim lapsed and that comes back: its next write on the item is refused `claim_lost`, naming the new holder (or "unclaimed"). It does not silently overwrite. It can reclaim only if the item is still unclaimed.

**`next_work` is safe to retry.**
- By default `next_work` returns the session's existing active claim, if it has one, instead of allocating another. A session asks for an additional item explicitly (`another: true`).
- `next_work` also takes an `idempotency_key`; a repeat with the same key returns the same allocation. A response lost after commit therefore never leaks a second claim.

**Resource holds track occupancy, not session chatter.**
- A hold's lifetime is its own TTL (set by the caller to cover the job, up to 24 h) and its own renewals (`renew_hold`, callable from a shell loop via `cg hold renew` during a long GPU run). It does **not** lapse because the session went quiet on other tools.
- When the holding session has been silent past the session threshold but the hold's TTL is live, the hold reads `held — owner unreachable since T` rather than `available`. Other agents see the difference and decide; CodeGraph still enforces nothing (P8, P13).
- A hold becomes available only on release, TTL expiry, or explicit `end_session`.

**Messages outlive sessions.**
- Messages about an item belong to the **item's thread** by default. A message to a session about an item is stored on that thread as well.
- States per recipient: `delivered` (returned by `inbox` or a brief), `read` (acknowledged by `ack`), `answered`/`declined` (for questions and requests). `inbox` replays from a cursor, so nothing is lost when a session crashes between delivery and handling.
- **Handover:** when an item's claim changes hands (recovery, release, pivot), its open questions and requests go to the new holder's brief. A request sent to yesterday's session is in today's worker's `get_work`.
- Direct-to-session messages with no item are kept for 7 days after the session ends and then shown to the next session of the same project and repository.

---

## 6. MCP tool surface (new)

Sixteen tools. All writes return the resulting object. All schemas reject unknown fields with a message naming the field.

| Tool | Does |
|---|---|
| `start_session` | Register session; return session id + briefing (budget default ~1,500 tokens) |
| `end_session` | Write handoff, release claims and holds |
| `get_briefing` | Re-fetch the briefing mid-session (e.g. after compaction), optionally focused on an item or paths |
| `next_work` | Pick and claim the best Ready, unblocked, unclaimed item for this project (filters: repository, type, labels, needs-resource) |
| `find_work` | Search/list items and features; small rows by default |
| `get_work` | The item brief (§5.2): AC, inherited requirements, relevant decisions/rules, touched files/nodes, handoffs, attempts, linked PRs, messages |
| `create_work` | One item, or a whole plan (feature + requirements + docs + items + deps), idempotent |
| `sync_plan` | Re-plan: diff a revised plan by `ref`, return a preview, apply on confirm; may edit/close in-progress items and notifies their holders |
| `update_work` | Patch fields, status, AC, links, claim/release, plus the save-point fields of §5.8 (`decisions`, `attempts`, `corrections`, `findings`, handoff); `expected_version` required |
| `record` | Write a knowledge entry with no item attached (any kind in §5.3) |
| `recall` | Search knowledge scoped by project / repository / paths / graph nodes / kind / source; flags stale entries |
| `post` / `inbox` / `ack` | Send a message (to an item thread, session, channel or `human`); read since cursor with long-poll; acknowledge |
| `changes` | Pull the project change feed since a cursor, with optional long-poll |
| `acquire` / `renew_hold` / `release` | Resource holds with their own TTL (§5.9) |
| `promote` | Propose (agent) or approve/reject (human) a lesson becoming a rule; mark a lesson `addressed_by` a local skill/hook/test |
| `challenge` | Agent disputes an active rule with evidence; you uphold, amend or retract it |

The existing graph tools stay. The briefing is where they start paying off: it can include "files you're likely to touch have these hotspots / these dependents" from the graph without the agent asking.

Also exposed over REST, and as a small **`cg` CLI**, for hooks and for agents without MCP.

---

## 7. Evidence and automatic state (P3)

| Event | Effect |
|---|---|
| Branch pushed whose name contains `SW-142` | Branch linked; item → In Progress if it was still earlier |
| PR opened referencing the item | PR linked; item → In Review if it was still earlier |
| PR merged | Merge recorded as evidence; item flagged `merged_awaiting_acceptance` until an agent marks it Complete |
| PR closed unmerged | Item → Ready if it was In Review; `attempt` drafted from the PR description |
| Re-plan edits or closes a claimed item | Current attempt superseded; holder notified on the item thread; later evidence from that attempt is recorded but moves nothing |
| Item In Progress with a lapsed claim | Becomes recoverable; offered first by `next_work` |
| All items of a feature Complete or Closed | Feature → Complete |
| PR review or review comment (human or bot) | Linked to the item as evidence with source and priority; no model call (§5.7) |
| Correction recorded from your prompt | Active rule at once, listed in "recently learned" |
| Rule challenged with evidence | Shown with the challenge in briefings; queued for you |
| Session heartbeat lapses | Claims and holds released, item keeps its last handoff |
| Item In Progress, no activity for N days | Flagged stale in UI and briefings |
| Files behind a knowledge entry change | Entry flagged `possibly_stale` |
| A lesson recurs while its rule was served to the session | Shown on the Lessons page as a candidate for a local fix |

Evidence never moves an item backward past a status an agent set, except a closed-unmerged PR. Uses the existing GitHub webhook (`GitHubWebhookController`), extended with pull request and pull request review events.

---

## 8. Human visibility

New UI areas (Angular, alongside the existing pages):

1. **Now** — live sessions (who, which harness/model, which item, what they last did), resource holds and waiters, the few items actually **blocked on you**, and a **review queue** (autonomous decisions, challenges, proposed rules) that nothing waits on.
2. **Project** — goals, member repositories (owned/dependency), features with progress, decision log, constraints, recent activity.
3. **Feature views** — board (columns by status; drag to move), **dependency graph** (what blocks what, critical path, what can run in parallel), and progress (done/total, what's blocked and why). Filter by repository/type/label/claimed-by.
4. **Item detail** — AC, timeline (status changes, commits, PRs, reviews, messages, handoffs, attempts).
5. **Message board** — per-project channels; you can post, answer, and mention a session.
6. **Knowledge** — browse/search decisions, facts, attempts; stale flags; supersession chains.
7. **Lessons** — by source, priority, recurrence and tier; last time each rule fired; rules that recur despite being in context; lessons not yet addressed by any skill.

---

## 9. Harness integration

CodeGraph's side is the interface: MCP tools, REST, and a small `cg` CLI (`cg brief`, `cg inbox`, `cg end`) that a hook or a shell-capable agent can call. Every work-tool response also carries an `unread: n` count, so a pending message is visible without any hook.

Everything about *when* those are called — hooks, instructions, skills — is configured locally in each harness and is not CodeGraph's responsibility (P13). The likely local setup, for reference only:

| Harness | Local setup |
|---|---|
| Claude Code | `SessionStart` hook running `cg brief`; skills carrying the work and coordination norms |
| Codex, others | AGENTS.md line to call `start_session` first; hook support varies (I do not know Codex's current hook surface) |
| Anything with a shell | `cg` CLI |

What CodeGraph does owe the local setup: `cg brief` returns data, not directives; omits empty sections; times out within 2 s; and prints one line if CodeGraph is unreachable.

The global CLAUDE.md workflow and the `shortcut-plan` / `shortcut-story` / `shortcut-epic` / `sceneworks-epic` skills get rewritten (by hand, per §5.7) for CodeGraph — much shorter, because the rules they spend most of their text on (read back every write, attribute comments, don't hardcode workflow states, there is no Blocked state) become properties of the system. The agent-to-agent coordination norms from §5.5 go into the same skills.

---

## 10. Migration

### 10.1 Shortcut (the main migration)

Most of the work lives in Shortcut. Sized on 2026-10-29 (non-archived only):

| What | Open | Done | Total |
|---|---|---|---|
| Epics | 156 | 243 | ~399 |
| Stories | 1,523 | 5,777 | ~7,300 |

One story workflow (`Standard`: Backlog, To Do, In Progress, In Review, Done) and one epic workflow (to do, in progress, done). Teams include SceneWorks, DarkFactory, LensWorks and CodeGraph. Iterations, objectives and custom fields are not in use. Stories were written by you and by separate Claude and Codex accounts.

**Mapping**

| Shortcut | CodeGraph |
|---|---|
| Team | Project, through a mapping table you approve in the dry run (teams don't line up exactly with projects: DarkFactory also holds OMP Desktop, Simple Router and SystemOne work; some stories and epics have no team). Unmapped → a holding project, listed for you. Each row also names the repositories its project owns; the dry run proposes them from the repositories the team's stories name (sc-27928). The table can also alias retired repositories to the indexed repository their code moved to (`mlx-gen` → `inference`), so stories naming them get that repository. The report shows where each team's work actually lands (a story follows its epic's project) and flags teams mapped to one project (sc-27931). |
| Epic | Feature. Description kept verbatim. Its requirements are imported as **unreconciled**: the feature is flagged until a planning agent extracts E1…En and acceptance tests into structured form (a normal agent task, created automatically for open features). Done epics stay Complete without reconciling. |
| Story | Item. Name → title, description verbatim, labels kept. |
| Story type feature / bug / chore | story / bug / task |
| Workflow state Backlog / To Do / In Progress / In Review / Done | Backlog / Ready / In Progress / In Review / Complete |
| Story relations `blocks` / `is blocked by` | `blocked_by` dependencies. Shortcut has no Blocked state; its blocked flag comes from these relations, so it maps straight onto the derived Blocked |
| Story relations `relates to` / `duplicates` | Links between items (`duplicates` noted in a comment) |
| Shortcut tasks (checklist) | Structured acceptance criteria, keeping each one's done/not-done state |
| Sub-tasks (child stories) | Items with a `blocked_by` edge from the parent |
| Comments | Comments, with original author and timestamp; authored as human or agent by account (you / Claude / Codex) |
| Branches, PRs, commits, external links | Item links (`branch` / `pr` / `other`) |
| Repository | Inferred from linked PRs/branches/commits, then GitHub links in the description, among the project's owned repositories (the table's are attached before items are written; only repositories CodeGraph indexes, and never one another project owns); otherwise from the project's single owned repository; otherwise left empty |
| Owners, requester | Recorded on the item as history; not converted into claims |
| Estimates | Dropped |
| Created / updated / completed times | Kept |
| Shortcut Docs linked to an epic | Feature documents |
| File attachments | Downloaded and stored with the item (Shortcut file URLs need Shortcut auth, so links alone would rot) |
| `sc-NNNN` | Kept as an alias: `get_work("sc-26226")` resolves, and GitHub evidence matching recognizes `sc-NNNN` in branch names and PR titles as well as new keys, so in-flight branches keep linking. Epics share the number space: `sc-N` of an imported epic resolves to its feature (sc-27930) |

**Imported items are an explicit exception to the new-item schema:** they may lack AC, repository, `inherits` and `touches`, and are marked `imported`. An agent that picks one up fills the gaps first (in the same `update_work`).

**Scope:** every non-archived epic and story, open and done. Done work is searchable history — closeout comments, PR links, what shipped and why — and agents planning new work need it. Archived items are skipped. One archived after it was imported is closed by the next sync (a finished one stays finished), and an archived epic's feature is flagged `imported.archived` with its reconcile task closed (sc-27929).

**Procedure** (re-runnable; keyed by Shortcut id, so a second run updates rather than duplicates):
1. **Dry run** — read everything, produce the mapping report: team → project table, counts per project and status, items with no inferable repository, unmapped relations, attachment volume. Nothing is written.
2. **You review** the report and the team → project table (the one place a human decision is needed).
3. **Full import** — writes everything; Shortcut is untouched.
4. **Delta syncs** — re-run with `updated_since` until cutover, so work done in Shortcut meanwhile carries over.
5. **Cutover** — local skills/instructions switch from Shortcut to CodeGraph; one last delta sync; Shortcut becomes a read-only reference.

The importer runs as a CodeGraph job using the Shortcut REST API. The MCP hub already has Shortcut tools (`shortcut_search_epics`, `shortcut_search_stories`), so its credential store is the likely place for the token; whether a token is configured there is not yet checked. It stays under Shortcut's rate limit (200 requests/minute); at ~7,300 stories plus epics, docs and files, a full run takes on the order of an hour (low confidence; depends on comment and attachment volume).

### 10.2 KanbanBoard

Only the SceneWorks Code Review 2026-10-27 epic is imported (mapping in §5.2a). Everything else stays in KanbanBoard.

### 10.3 Native harness memory

Import from Claude Code `~/.claude/projects/*/memory/*.md` and Codex equivalents, verified before anything is stored:
1. Parse each memory into a candidate entry: kind, project/repository scope, applies-when, source file.
2. Verify each candidate against the current state: referenced files, functions, commands, flags, hosts and paths still exist; facts not contradicted by the code or by existing entries. Result per candidate: `verified`, `stale` (with what no longer matches), `unverifiable` (e.g. infra not reachable from the repo), or `personal` (a preference that belongs in harness memory, not shared knowledge).
3. You review the batch with the verification result next to each candidate and accept, edit or drop. Only accepted entries are stored, with provenance pointing at the source file and `verified_at` set to the commit checked against.
- The verification is done by the importing agent in its session (reading the repo, running the checks); CodeGraph makes no model call (P12). CodeGraph's part is a dry-run endpoint that takes the candidate batch, checks scope and dedup against existing entries, and stores nothing until confirmed.
- Source memory files are not modified or deleted.

---

## 11. Requirements (numbered, for review)

**Projects and repositories**
- R1. A Project groups 1..n repositories, each `owned` or `dependency`, and has goals. Unassigned repositories behave as today.
- R2. `repository` means a repository everywhere: DB columns, C# identifiers, MCP, REST, UI. `project` means only the new Project. An old `project` parameter that meant a repository is rejected with an explanatory message.

**Orientation**
- R3. `start_session` registers a session and returns a briefing within a configurable token budget, ranked for the repository, branch, paths and stated intent.
- R4. Briefing includes: project goals, relevant decisions/rules (with any open challenge), my claimed item with its latest handoff, unread inbox, other live sessions in the project and what they hold, relevant resource holds, and one suggested next item. Empty sections are omitted.
- R5. A `cg` CLI (`brief`, `inbox`, `end`) over REST for local hooks and shell agents; it times out within 2 s and prints one line if CodeGraph is unreachable.

**Work**
- R6. Features hold numbered requirements and acceptance tests and may span repositories; items belong to one owned repository and hold 1–10 AC, `inherits`, `touches`, `blocked_by`.
- R7. `create_work` imports a full plan in one idempotent, all-or-nothing, cycle-checked call. `sync_plan` diffs a revised plan by `ref`, previews, and applies on the caller's confirmation; it may edit or close in-progress items and notifies their holders.
- R8. `next_work` atomically selects and claims the best recoverable or Ready, unblocked, unclaimed item; returns the session's existing claim unless `another: true`; takes an `idempotency_key`. `get_work` returns the self-contained brief of §5.2.
- R8a. Recovery per §5.9: lapsed In Progress items are recoverable with branch, PRs, latest handoff and unmet AC; a returning session whose claim lapsed gets `claim_lost` naming the new holder.
- R9. Claims are leases bound to a live session and lapse on heartbeat loss.
- R10. Every write returns the resulting state; unknown parameters are rejected with a message naming them; updates need `expected_version`. Every work-tool response carries the session's unread message count.
- R11. Statuses Backlog / Ready / In Progress / In Review / Complete / Closed; Blocked derived. Agents and orchestrators can make every transition. GitHub evidence attaches links and moves lagging items forward per §7.
- R11a. A pull-based change feed (`changes`) with cursor and long-poll covers every work change.
- R11b. Work tracking is ported from KanbanBoard, which is then retired.
- R11c. Only Complete satisfies a dependency; dependents of a Closed prerequisite show `cancelled prerequisite` until re-planned. Feature completion also requires every requirement covered by a Complete item or explicitly dropped.
- R11d. A merge records evidence and sets `merged_awaiting_acceptance`; only an agent (or human) marks an item Complete, optionally with an acceptance report. CodeGraph does not validate the report.
- R11e. `sync_plan` confirmation is bound to a versioned preview token; stale confirmations are refused. Re-plans mark the current attempt superseded; evidence from superseded attempts moves no status.
- R12. Handoffs (done, decisions, open issues, corrections, next action) and attempts are first-class and shown on the item and in briefings.

**Knowledge**
- R13. A new knowledge store, scoped to a project and its repositories (not to a user), shared by every agent and person with access. Existing access model kept: any valid PAT sees all projects.
- R14. `update_work` and `end_session` accept the save-point fields of §5.8 and store them as typed, scoped, attributed entries synchronously; `record` covers knowledge with no item.
- R15. `recall` filters by project, repository, activity, paths, graph nodes, kind and source; results carry freshness and confidence.
- R16. Entries whose underlying files changed since verification are flagged `possibly_stale`.
- R17. Entries are superseded or retracted, never silently overwritten; revisions are kept.

**Coordination**
- R18. Messages: channels per project/feature/resource, direct-to-session and `human`; kinds per §5.5; blocking questions block their item. No assignment between agents.
- R19. `inbox` supports cursor reads and long-poll; the UI gets live updates. Messages about items live on the item thread; per-recipient states delivered / read (`ack`) / answered / declined; open requests hand over to the item's next holder (§5.9).
- R20. Resource holds with capacity, FIFO+priority waiters, their own TTL and renewal, and an `owner unreachable` state distinct from `available` (§5.9). Informational only.
- R21. Overlapping work-area claims between live sessions (direct or via impact radius) produce warnings to both, naming the other session.

**Learning loop**
- R22. Lessons come from your prompts (highest priority), PR reviews (yours, then bots), subagent reviews and other agents, recorded by the agent through save-point fields. Review comments are linked to items by webhook. No LLM call is made for capture or extraction; commits are not a correction source.
- R23. Applies-when has an activity dimension and an optional location dimension, and is required for a lesson to leave task scope. Embedding similarity returns merge candidates only; merging requires the recording agent's `same_as` or an exact normalized match; `contradicts` keeps both and flags them. Recurrence counts distinct source events.
- R24. A lesson from your prompt is an active rule immediately and appears in "recently learned" for edit/retract. Other lessons become rules on recurrence or approval.
- R25. `challenge` attaches evidence to an active rule; the rule stays active, is shown with the challenge in briefings, and goes to your review queue to uphold, amend or retract.
- R25a. Decisions marked `made_autonomously` appear in the review queue; nothing in the review queue blocks work.
- R26. Briefings log the rules they served; the Lessons page shows recurrence with and without the rule served. CodeGraph takes no action on it.
- R27. Rules have no time decay; they leave briefings only when superseded, retracted or addressed by a skill.
- R28. CodeGraph never generates or edits skills, instructions, hooks, tests or CI; it provides grouped lesson queries and `addressed_by` links to them.

**Human**
- R29. UI pages per §8, including blocked-on-you, the review queue, "recently learned", and board / dependency graph / progress views per feature.
- R30. Humans can perform every agent action from the UI; all actions show actor (human or session).

**Migration**
- R31. Shortcut importer per §10.1: all non-archived epics and stories (open and done), dry run with mapping report first, approved team → project table, re-runnable delta syncs keyed by Shortcut id, attachments downloaded, `sc-NNNN` aliases resolved by `get_work` and by GitHub evidence matching.
- R31a. KanbanBoard import of the SceneWorks Code Review 2026-10-27 epic only.
- R32. Native-memory import as a dry run first: candidates carry a verification result (`verified` / `stale` / `unverifiable` / `personal`) and dedup matches; nothing is stored until you accept each entry. Source files are untouched.
- R33. The existing memory MCP tools are hidden from the default listing; their data is kept.

Out of scope for CodeGraph (local, per P13): hooks, instructions and skills that decide when agents call CodeGraph, fill save-point fields, check the inbox, challenge rules, respect holds, judge when an item is Complete, or how to word a recorded correction. Rewriting the Shortcut-based skills is tracked as part of the terminal slice but lives in the harness config, not in CodeGraph.

---

## 12. Decisions and open questions

### Decided (2026-10-29)

| # | Topic | Decision |
|---|---|---|
| D1 | Naming | Repository means a git repository. Project is a higher-level container of many repositories, owned and dependency. |
| D2 | Knowledge store | New store, scoped to project and repositories, not to users. The existing memory system is not the base for it. |
| D3 | Hosting | Inside `CodeGraph.Api`. CodeGraph becoming critical infrastructure is intended; it is owned and fixable. |
| D4 | Strictness | Loud, explicit failures; they tell the agent what to fix. |
| D5 | Leases | Informational. Ignoring a hold is the agent's risk. |
| D6 | Agent-to-agent | Coordinate and request only, never assign or direct. Norms live in instructions/skills. |
| D7 | Status | In Review = PR open. ~~Merged = Complete~~ — revised by D27. |
| D8 | Corrections | From prompts (yours highest) and code reviews (GitHub AI, yours, subagents), possibly other agents. Not from commits. |
| D9 | Dedup | Similarity plus applies-when overlap. |
| D10 | Skills | Manual, between you and an agent, informed by lessons. |
| D11 | Rename depth | Rename all the way down: DB, C#, MCP, REST, UI. |
| D12 | Your corrections | Active immediately; agents may challenge with evidence; you can be reasoned with. Most will be about process. |
| D13 | Memory lessons | Shared cross-harness knowledge is valuable; CodeGraph's current memory system is not the right shape. Never ask "when to save"; capture at fixed save points. |
| D14 | Cost | No extra LLM call per prompt or per event. |
| D15 | Access | Keep: any valid PAT sees all projects. |
| D16 | Enforcement | Not CodeGraph's job. It stores, relates, serves and reports; the harness enforces via instructions, skills and hooks. |
| D17 | Existing memory tools | Hidden from the default MCP listing via entitlements; data kept. Retirement or migration decided later, only if the data proves worth it. |
| D18 | Native harness memory | Import selected Claude/Codex memory files to seed the store, each entry verified before it is stored (§10). |
| D19 | Tracker base | Port KanbanBoard into CodeGraph rather than write a tracker from scratch or keep a separate service. |
| D20 | Work model | Discrete items in a dependency graph with a computed ready queue; the board is a human view. Plans enter as documents; items are the source of truth. |
| D21 | Autonomy | No human-only operations; agents drive status; nothing waits on you by default; decide-and-record over blocking. |
| D22 | Re-plans | May edit or close in-progress work; holders are notified. |
| D23 | KanbanBoard data | Import only the SceneWorks Code Review 2026-10-27 epic; the rest stays in KanbanBoard. |
| D24 | Shortcut | Full migration of the Shortcut workspace; it holds most of the work. |
| D25 | Shortcut scope | Import open and done work (all non-archived, ~7,300 stories): history is needed for planning. |
| D26 | Blocked on import | Derived from `blocked_by` edges; blocked-without-edges imports as Backlog with the reason in a comment. |
| D27 | Completion | A merge does not complete an item; Complete is the agent's discretion. Acceptance report optional, not validated by CodeGraph. |
| D28 | Correction provenance | CodeGraph does not verify where a correction came from; it stores what the agent writes. Whether and how the agent records your words (paraphrased, not necessarily verbatim) is a harness-side decision. |

### Still open

None. Next step: approve the requirements, then turn §13 into a plan.

---

## 13. Rough slicing (not yet a plan)

1. Port KanbanBoard's store, items, dependencies, claims, plan import and change feed into CodeGraph (after reading its domain code).
2. **Walking skeleton across two harnesses**: sessions + `start_session` / `next_work` (retry-safe) / `update_work` / `get_work` brief + minimal briefing + `cg brief`, exercised by a Claude Code session and a Codex session on one project. First end-to-end proof, before anything else is built on top.
3. Project/repository rename all the way down (DB, C#, MCP, REST, UI) + Projects with repository membership.
4. Features, requirements, acceptance reports, Complete-only dependency satisfaction, `sync_plan` with versioned preview and superseded attempts, derived Blocked/Ready.
5. Recovery of lapsed work, `claim_lost`.
6. Knowledge store + save-point fields + `record`/`recall`, scoping, supersession.
7. Freshness flags from file hashes; graph-scoped recall.
8. GitHub evidence: links, forward-only status fill-in, review comments as evidence, superseded-attempt handling.
9. Messages: item threads, delivery/ack/answer states, replay, handover, long-poll, `human` channel, unread counts.
10. Resource holds with independent TTL/renewal and `owner unreachable`.
11. Overlap warnings via `touches` + impact radius.
12. Lessons: applies-when, candidate-only similarity with `same_as`/`contradicts`, distinct-event recurrence, rules, challenges, served-rule log, `addressed_by`.
13. UI: Now, Project, feature views (board, dependency graph, progress), Item, Messages, Knowledge, Lessons, review queue.
14. Importers: Shortcut (dry run → your review of team → project mapping → full import → delta syncs), the KanbanBoard code-review epic, and native memory (dry run → verify → your review → store); hide existing memory tools from the default listing.
15. Terminal: cutover after the scenario acceptance below passes; retire the Shortcut/Kanban workflow; local skills rewritten (outside CodeGraph).

**Scenario acceptance before cutover** (two harnesses, real MariaDB):
- crash recovery: kill a session mid-item; a second session recovers it with branch, handoff and unmet AC; the first returns and gets `claim_lost`;
- allocation retry: drop the `next_work` response after commit; the retry returns the same item, no second claim;
- long GPU job: a hold renewed by `cg hold renew` survives 30+ minutes of tool silence and shows `owner unreachable`, never `available`;
- message handover: a request sent to session A is in session B's brief after B recovers the item;
- concurrent re-plan: two agents confirm previews of the same feature; one wins, the other is refused stale; a PR from the superseded attempt merges and moves nothing;
- contradictory lessons: "always X" and "never X" with the same scope stay separate and are flagged; a retried correction counts once;
- one prompt correction in session A is served as a rule to session B.

Effort (low confidence, agent-driven, one story at a time): 1–2 days for the skeleton; 3–5 weeks of elapsed work for all fifteen (the KanbanBoard port should save time against writing a tracker; how much depends on the code read), with the deep rename about 2–4 days of that.
