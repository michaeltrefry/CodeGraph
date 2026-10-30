# Plan: Link indexed web and desktop UIs to backend operations

**Repository:** `michaeltrefry/CodeGraph`
**Reviewed:** 2026-10-30, against indexed CodeGraph/main `9050ca9` and representative files at the fleet's recorded index revisions.
**Confidence:** High for the observed gaps and repository patterns; repository indexes have different ages and are not proof of their latest upstream state.
**Status:** Approved plan recorded in [Shortcut epic sc-27972](https://app.shortcut.com/trefry/epic/27972). Execution is in progress, starting with sc-27973. Shortcut is the operational source of truth for current states and evidence.

**Objective:** Given a changed frontend or backend file, identify affected UI components, screens and actual browser routes, and trace their HTTP requests or Tauri operations to backend implementations. Support applications without browser routing as well as routed web apps.

The original Angular reference is TC `ai/tc.codegraphapi` MR !168, commit `e2593c8`, with qualified-name resolution from `8b9db12`. Reuse applicable behavior/tests through this repository's resolver, discovery boundary and incremental persistence. TC-specific conventions are not assumptions about every indexed app.

## 1. Repository evidence and current gaps

The live inventory contained 42 indexed entries, including database schemas. These representative source examples establish the relevant patterns; not every indexed repository contains a frontend.

| Indexed repositories | Verified pattern | Required linking |
|---|---|---|
| CodeGraph, CodeFlow | Angular: `CodeGraphWeb` and `codeflow-ui` | Routes, templates, services, DI and HTTP |
| SceneWorks | React JS/JSX; `apps/web/src/App.jsx` selects `activeView`; `api.js` wraps fetch | Components, hooks, screens and HTTP wrappers |
| ChatWorks | React JS/JSX; context, named screens, Tauri invoke and event imports | Components, context and native operations |
| SoundWorks | React TS/TSX; `apps/web/src/tauri.ts` wraps commands | Components, screens and Tauri wrappers |
| LensWorks catalog desktop | React TS/TSX; `engines/catalog/desktop/src/bridge.ts` | Components and Tauri commands; do not infer the whole product uses React |
| router | Next.js App Router; `frontend/src/app`, `/ui` base path, `src/lib/api.ts` wrapper | Filesystem pages/layouts and HTTP to its Go backend |
| twitter-clone | React Router JSX in `frontend/src/App.tsx` | Browser routes, page components and requests |
| ui | Shared `@sceneworks/ui` React JS/JSX package | Export/import and render dependencies; no invented page routes |

Confirmed CodeGraph/main gaps:

- `DiscoverTypeScriptScope` admits TS/TSX, tsconfig and package manifests, but sidecar `isTsSourceFile` accepts only TS, and `allowJs` is false. The pipeline invokes project analysis only with an admitted tsconfig. JS-only apps and TSX require support throughout this path, not just another glob.
- Sidecar extraction covers classes, interfaces, enums and limited Angular routes. HTTP scanning runs inside class members. Ordinary functions, function components, custom hooks and their calls are missing from this path.
- Angular routing records own segments with `component:` in typed arrays. It misses composed/lazy route trees. Selector/template metadata exists, but render edges, token bindings and interface dispatch do not.
- HTTP scanning targets Angular HttpClient and TC ApiContract conventions. It does not trace the observed fetch, apiFetch and request function chains.
- TypeScript targets often use bare names or rendered types rather than declaration QNs. Reuse `workspaceResolver.ts`, including its alias/workspace resolution, instead of introducing a second resolver.
- Whole-project analysis derives cross-file facts, but incremental persistence replaces file slices. Existing module-slice refresh does not reconcile general derived relationships.
- `CrossRepoLinker.LinkHttpRoutesAsync` does not distinguish UI pages from backend endpoints and skips same-repository matches. It cannot alone deliver the required binding.
- Existing graph vocabulary includes Component, Function, Method, Route, CALLS, RENDERS, HANDLES, REGISTERS and SUBSCRIBES. Reuse graph storage, MCP and impact services; impact currently does not follow REGISTERS.

## 2. Shared requirements

- **E1 — Coverage:** Read admitted JS/JSX/TS/TSX, configurations and referenced Angular HTML. Work without installed node_modules or tsconfig. Preserve discovery exclusions and checkout-independent identities.
- **E2 — Dependencies:** Resolve components, functions, hooks, JSX/templates and framework-specific providers. Preserve ambiguity/evidence; never bind solely by a common symbol name.
- **E3 — Navigation:** Model Angular, React Router and Next.js browser paths, including app base paths. Keep state-selected screens traceable without fabricating URLs.
- **E4 — Backend operations:** Resolve direct/wrapped HTTP to endpoints within and across repositories, and Tauri calls to registered Rust commands. Distinguish navigation from endpoint identity.
- **E5 — Incremental correctness:** Additions, removals, retargeting and semantic property changes must match clean extraction of the same admitted source. Incomplete analysis must not erase valid facts.
- **E6 — Delivery:** Reuse the existing analyzer/resolver/persistence surfaces, bump affected extraction versions, rebuild committed dist, and upgrade indexes through routine sync. Validate source/graph behavior without application workloads or inference.

Static analysis describes possible dependencies, not exact runtime execution. Proven declaration/command bindings can be high confidence; conditional providers and dispatch need candidate/confidence metadata. Unsupported dynamic expressions remain explicitly unresolved.

## 3. Implementation slices

Four proposed feature stories cover distinct implementation boundaries. They are not four mandatory PRs; related stories may share review and CI. No separate discovery, diagnostics, benchmark or terminal-readiness story is needed.

| Slice | Outcome | Requirements | Depends on |
|---|---|---|---|
| [S1 / sc-27973](https://app.shortcut.com/trefry/story/27973) | Trace React components through functions, hooks and context with incremental refresh | E1, E2, E5, E6 | None |
| [S2 / sc-27974](https://app.shortcut.com/trefry/story/27974) | Trace direct/wrapped HTTP to backend endpoints | E4, E5, E6 | S1 / sc-27973 |
| [S3 / sc-27975](https://app.shortcut.com/trefry/story/27975) | Add framework routes and complete Angular template/DI relationships | E1, E2, E3, E5, E6 | S1 / sc-27973 |
| [S4 / sc-27976](https://app.shortcut.com/trefry/story/27976) | Trace Tauri wrappers and registered Rust commands/events | E4, E5, E6 | S1 / sc-27973 |

S2–S4 extend S1's extraction/refresh path. Final acceptance reuses their evidence and completes in the last delivered slice.

### S1: Components, calls and incremental foundations

**Primary paths:** sidecar extractor/workspaceResolver/protocol, TypeScript analyzer/server contracts, `IndexingPipeline.TypeScript.cs`, `ExtractionVersionCatalog`, and existing pipeline/impact tests.

- Admit JS/JSX/TS/TSX, package/config files and HTML through discovery. Read HTML only when referenced by an admitted Angular component. Support tsconfig/jsconfig and a default parser configuration for apps with neither. Do not execute config files or install dependencies.
- Update compiler options, source selection, request contracts, processed-file accounting, fallback coordination, exclusion cleanup and version coverage together. Successful repository analysis must not be duplicated by per-file fallback or advance hashes for unprocessed files.
- Reuse declaration/import resolution for aliases, barrels and workspace packages. Preserve existing QNs where possible. New callable identities include module and lexical owner, distinguish same-named symbols and give default exports stable identities.
- Emit callables for declared/arrow functions, members and hooks; classify React component callables without disconnected duplicate nodes. Resolve local/imported calls and callbacks, including effect/event-handler bodies and observed memo/forwardRef/lazy patterns. External library source must not be required.
- Emit RENDERS from JSX component references, including aliases/namespace imports. Native HTML tags are not components. Keep package/import dependencies for external components; bind indexed cross-repo exports only when identity is unambiguous.
- Model createContext, providers and useContext/custom-hook consumers with existing primitives and context/provider metadata. Connect provided callable values when statically supported. Preserve provider-tree scope, nested overrides and alternatives; do not globally bind every consumer to every implementation.
- Resolve Angular INHERITS, IMPLEMENTS, constructor/inject INJECTS and existing HANDLES to declaration QNs too. Keep framework/external filtering; arbitrary rendered type text must not become an application stub.
- Deliver section 4's incremental mechanism now. Verify impact traversal through component/callable ownership and dependencies; add only traversal needed for the acceptance paths.

**Acceptance:**
1. Representative JS/JSX and TS/TSX components connect through rendering, imported functions/hooks and context, including an app without tsconfig.
2. Aliases, duplicate names and provider alternatives retain correct targets or explicit uncertainty; excluded files are never read.
3. Cross-file changes/deletion, unchanged reruns and sidecar failure satisfy section 4.

### S2: Direct and wrapped HTTP

**Primary paths:** shared callable/URL analysis, `IndexingPipeline.Resolution.cs`, `CrossRepoLinker.cs` and route/linking tests. Inspect backend extraction for each acceptance path and repair missing endpoint identity within this slice if needed.

- Scan all callable bodies for HttpClient, existing ApiContract calls and native fetch, including method/options and default GET.
- Infer bounded wrapper summaries from resolved bodies and substitute arguments through observed chains: SceneWorks apiFetch(path, token, options), router request(path, init), and their exported helpers. Propagate method overrides/defaults, object spreads and path parameters. Names alone are not wrapper recognition rules.
- Evaluate literals/templates, concatenation, locals/fields, imported/static constants, nested endpoint objects, string enums and new URL(path, base) with href/toString. Use scope/symbol-aware caches and recursion guards; unrelated identifiers named url cannot collide.
- Preserve known prefixes such as /api/v1 and /admin/v1. Normalize query/fragment and origins separately; retain origin/service evidence for target selection. Unknown segments become placeholders only when a meaningful static endpoint remains. Fully unknown URLs/methods stay unresolved.
- Bind eligible same-repository endpoints as well as cross-repository endpoints. Reuse existing matching/ambiguity rules and eligible persisted nodes, excluding deleted/replaced routes. Ties remain candidates, not confident links.
- Exclude frontend route kinds, including legacy Angular page routes, from endpoint matching. Preserve legacy backend routes lacking kind metadata. This protection ships with binding, not as a follow-up after new UI routes exist.

**Acceptance:**
1. A SceneWorks component/helper reaches its Rust endpoint, and router's wrapper preserves /admin/v1, path arguments and method overrides to its Go endpoint.
2. HttpClient/ApiContract behavior remains correct; UI routes and ambiguous repositories cannot create false endpoint bindings.
3. Wrapper-only, imported-constant and backend-route changes update/remove dependent bindings through routine indexing.

### S3: Framework routes and Angular dependencies

**Primary paths:** framework passes, shared resolver, scoped HTML reads and impact traversal. Detect framework use from admitted imports/package/config evidence, not every array named routes.

- **Identity:** Preserve backend endpoint QNs. Frontend Route QNs use a separate ui-route prefix with repository, app root, framework and full path; properties include route_kind=ui, framework, full_path and source ownership. This prevents UI/API and multi-app collisions. HANDLES connects routes to pages; RENDERS connects layouts/children. Browser navigation with GET does not make a page an API endpoint.
- **Angular routes:** Preserve the original port scope: typed/untyped arrays, forRoot/forChild/provideRouter, literal/identifier/spread children, loadChildren/loadComponent, named/default lazy exports, legacy path#Module, and imported routing NgModules. Use bounded cycle-safe traversal, join paths, retain redirects/wildcards, deduplicate child roots and preserve component/lazy-module handlers through workspaceResolver and DiscoveryScope.
- **React Router:** Support twitter-clone's JSX declarations with imported page elements, nested/index routes, pathless layouts, parameters, wildcards/redirects and basename. Reuse composition for statically declared route-object APIs encountered during implementation; do not build a generic routing framework.
- **Next.js:** Derive App Router pages and applicable layouts from admitted paths, handling route groups, dynamic/catch-all segments and router's statically readable /ui basePath. Separate pages from real API route handlers. Read configuration as source, never execute it; report dynamic/unsupported configuration explicitly.
- **Screens:** Preserve SceneWorks/ChatWorks/SoundWorks render/call paths and useful declared screen-key metadata. Do not invent pathnames or add runtime state analysis to identify a screen.
- **Angular templates:** Cover inline/external HTML, element/attribute selectors, comments, defer blocks, statically resolved ngComponentOutlet, createComponent, dialog open and snackbar openFromComponent. Bind actual repo components, record template_path and reject out-of-scope reads.
- **Angular DI/dispatch:** Retain interface methods/function properties with DEFINES_METHOD; provide/useClass/useExisting registrations; token fields, inject, injector.get and typed runtime-token alternatives; calls through typed fields/locals; interface-to-implementation dispatch only for callable members. Respect provider scope. Proven bindings are high confidence; runtime/interface alternatives are medium-confidence candidates. Add inbound REGISTERS impact traversal and verify implementation-to-provider/page reachability.
- Replace partial Angular route nodes during versioned refresh, inspect dashboard/MCP QN assumptions, and include all derived facts in section 4 before shipping.

**Acceptance:**
1. CodeGraph or CodeFlow yields full lazy Angular routes and template/service/token relationships; twitter-clone yields its declared React Router paths.
2. router yields pages/layouts under /ui; state-selected screens remain traceable without invented URLs or API matches.
3. Parent-route/layout/template/provider-only changes remove stale facts and update affected slices; implementation impact reaches the relevant component/route.

### S4: Tauri operations

**Primary paths:** shared JS/TS callable/wrapper analysis, Rust extraction/registration metadata and existing graph resolution/impact. Reuse indexed Rust functions; inspect semantic/fallback output before adding extraction.

- Resolve imported/aliased invoke and statically named wrappers in ChatWorks, SoundWorks and LensWorks. Propagate command strings through helpers such as SoundWorks readOverview(command, fallback); keep callable-to-wrapper edges.
- Bind commands exposed by the app's Rust registration, including declared renaming/namespacing. A same-named function in another app/crate is insufficient. Use app-scoped command identities and CALLS with Tauri transport metadata, not fake HTTP routes.
- For statically named listen/emit events used by these apps, connect producers, event identities and subscribing callbacks with existing event edges. Scope names to the app; retain uncertainty for dynamic names.
- Preserve desktop dependencies when wrappers have browser fixture fallbacks, without claiming the fallback executes a backend operation. Reconcile wrapper, registration, command/event changes; failed/missing Rust analysis cannot imply command deletion.

**Acceptance:**
1. A ChatWorks or SoundWorks screen reaches a registered Rust command through its wrapper; LensWorks bridge commands bind within the correct app.
2. Named events on the selected paths retain producer/subscriber dependencies; ambiguous commands/dynamic names remain visibly unresolved.
3. Wrapper, registration and command/event rename/removal update bindings without stale links or loss on failed analysis.

## 4. Incremental correctness — required in every slice

Keep whole-project calculation and extend existing slice refresh; no separate dependency-tracking service is needed.

1. Retain successful output and its completeness/ownership, including edges. Compare stable QNs before ID/stub resolution. Partition nodes/outgoing edges by source slice; synthetic route/context/command facts need explicit owners.
2. Compare canonical semantic signatures against stored extraction-owned facts: node label/QN/path/location and meaningful properties; edge endpoints/type and meaningful properties. Include all new derived facts: render/handler/provider/injection edges, all CALLS, HTTP bindings, context and events. Route identities or interface-dispatch edges alone are insufficient.
3. Canonicalize sets/property ordering. Exclude timings, timestamps, diagnostics, IDs, generated analysis text and unrelated linker enrichment. Reuse provenance metadata or add the minimum owner marker needed to distinguish extraction from linker-owned output.
4. Add every differing slice in either direction to replacement paths. Remove stale facts after file/template/provider/export/route/command deletion even when the caller is unchanged. Reuse transactional replacement; preserve incoming edges and unchanged endpoints so replacing a target cannot drop surviving dependencies.
5. Analyze/reconcile relevant HTML/config/manifest/backend-only changes even with zero changed JS/TS hashes. Extend versions to JS/JSX and newly tracked inputs. HTML may remain non-hash-tracked only if routine sync observes template-only commits and recomputes its owner.
6. Refresh endpoint/command binding when either callers or targets change. Same-repo resolution sees current output plus eligible persisted nodes; cross-repo linking revisits affected inbound/outbound links when either repo updates. Do not compare raw extraction with enriched persisted links and create churn.
7. Delete by absence only after successful complete analysis of the relevant owner/scope. Sidecar unavailability, parse failure, missing configuration support and partial Rust analysis are not empty successful output. Preserve affected prior slices/bindings, report incomplete coverage and advance only successful source hashes.
8. An unchanged successful rerun produces no extra replacement slices. Log changed-slice counts and bounded unresolved diagnostics through existing logging.

Reuse fixture builders to cover distinct failures: callable/export retargeting; parent-route/layout changes; HTML-only rendering; provider-only edits; wrapper/constant HTTP changes; backend/command registration changes; deletion/exclusion; semantic-property-only changes; unchanged reruns; failed/partial analysis. Assert the persisted graph against fresh fixture extraction, not only counts or selected replacement paths.

## 5. Validation and rollout

- Extend existing sidecar and C# pipeline/linker/impact tests. Port applicable TC Angular tests in S3; TC-specific timing and raw route counts are not this fleet's acceptance criteria.
- Demonstrate persisted paths: SceneWorks component → helper → apiFetch → Rust endpoint; router page/layout under /ui → request → Go endpoint; Tauri screen → wrapper → registered command; CodeGraph/CodeFlow Angular page → template/service/token → endpoint. Include twitter-clone routes and shared-ui import/render checks in framework fixtures. Reuse this evidence for final acceptance.
- Verify through existing MCP/impact surfaces in the relevant directions. A disconnected node inventory or successful compile does not establish usable impact paths.
- Extend one bounded Link coverage JSON diagnostic: admitted/processed files by extension; routes/screens/components; render/call/context edges; direct/wrapped/bound/unresolved HTTP; Tauri commands/events; Angular DI/dispatch. Include bounded ambiguity samples. Library/orphan components are informational, not automatic failures. Reset per-run caches/counters in the long-lived sidecar.
- Bump TypeScriptProjectAnalyzer.ExtractionVersion for each shipped sidecar-output change (review baseline: 3), plus affected pipeline/Rust versions. Rebuild/commit dist. Verify routine sync upgrades unchanged revisions and removes obsolete Angular QNs/stubs; do not depend on manual reindexing.
- Run focused tests, sidecar build and required repository checks. Use existing timing logs on selected repositories for obvious regressions. No separate benchmark, full-fleet reindex campaign or inference execution is required.

Shortcut reconciliation on 2026-10-30 found no matching UI-to-backend epic. Reuse completed resolver epic sc-970 (including sc-976) and incremental replacement sc-17947. General Rust indexing epic sc-1384/sc-17897 and route extraction sc-1387 are related foundations; revalidate current source and coordinate any actual overlap without duplicating broad extractor work or treating stale tracker status as a blocker. The epic records these findings and all shared requirements; each story has its full slice scope and three acceptance criteria. The connector does not expose native dependency-link writes, so the epic and story descriptions explicitly record S1 blocking S2–S4, with no mutual dependency among S2–S4. Any pattern that cannot meet stated acceptance must be surfaced and tracked explicitly, not silently removed from scope.
