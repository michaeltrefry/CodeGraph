# CodeGraph — Enterprise Codebase Knowledge Graph

## Business Context

The company is a **domain name reseller and auctioneer**. The codebase spans domain availability checks, auctions/bidding, domain transfers, renewals, WHOIS lookups, DNS management, escrow/payments, and customer account management. Key external integrations include partner APIs for domain registries, GoDaddy, AWS services, and payment processors.

### Customer-Facing Products

- **HugeDomains.com** — ColdFusion application. The original product and largest customer base. Straightforward domain resale from an inventory of 3M+ domains.
- **DropCatch.com** — Customer-facing backorder and auction platform. Customers create backorders for expiring domains or participate in auctions for recently caught domains.
- **NameBright.com** — Full domain management platform (the company's equivalent of GoDaddy). Shares inventory with HugeDomains but also provides domain management — nameservers, host records, DNS, email. Customers buy, sell, and manage their domains here.

**Drop catching** ("playing the drop") is a major business operation supported by a significant number of repositories. When registered domains expire and are deleted by the registry, they briefly become available for re-registration. The company monitors expiring domains, evaluates their value, and attempts automated registration at the exact moment of deletion — competing with other registrars for the same domains. This involves expiration monitoring, domain valuation, precisely timed registration attempts against registry APIs (often multiple registrars for better odds), customer backorder systems, and queue/priority management. Repos related to drop catching are time-critical infrastructure.

**Domain Valuation** is a critical business process that determines the potential value of domains before the company commits to purchasing or catching them. It feeds into drop catching decisions (is this domain worth competing for?), inventory pricing, and auction reserve prices. The system has recently been augmented with AI-based processes. Only two people in the company currently understand the valuation system — making it a high-priority target for CodeGraph analysis and documentation. Expect repos involving scoring models, data pipelines pulling traffic/SEO/keyword signals, and integration with both the drop catching and inventory systems.

**EPP (Extensible Provisioning Protocol)** is the standard protocol for communicating with domain registries (Verisign, Afilias, etc.). It's the low-level plumbing underneath registrations, transfers, renewals, and availability checks — XML-based over TCP/TLS, with registry-specific extensions. Expect repos handling EPP connections, session management, and command/response parsing.

This context matters for analysis: when Claude sees `GetDomainAvailability`, `PlaceBid`, `TransferDomain`, `CatchDomain`, `BackorderRequest`, `EppCommand`, it should describe these in domain resale business terms, not generic technical language.

## Purpose

Nobody in the company knows what all 620+ repositories do. Institutional knowledge has left with former employees. Services call services that call services, and tracing data lineage or understanding what depends on what requires archaeology.

CodeGraph solves this by building and maintaining a complete knowledge graph of every repository in the GitLab instance — structural connections, business intent, data flow, and cross-service dependencies — queryable through an MCP server so that Claude becomes the domain expert the company no longer has.

## Core Design Principle: Self-Maintaining

**If it requires human attention to stay accurate, it will rot within weeks.**

Every component must answer: what happens when nobody is watching?

- New repos appear in GitLab → automatically discovered, cloned, analyzed, documented, and added to the graph
- Code is pushed → CI updates the graph and regenerates documentation via direct commit
- Repos are deleted or archived → automatically detected and removed from the graph
- Foundational framework repos change → downstream repos are re-analyzed automatically
- Analysis fails on a repo → logged, skipped, does not block the rest of the pipeline
- A repo hasn't been touched in years → the graph reflects that staleness, doesn't silently carry outdated data

No merge requests to approve. No manual registration. No human in the loop for steady-state operation.

---

## What CodeGraph Produces

### 1. The Knowledge Graph (MySQL)

A queryable graph of every structural connection point across all repositories:

- Public API endpoints (routes, HTTP methods, request/response models)
- Services and their DI registrations
- RabbitMQ messaging — events, publishers, consumers, queues, exchanges
- Shared NuGet package dependencies (especially `TC.*.Models` contracts)
- Database tables, views, stored procedures, and which services query them
- Scheduled jobs and what they call
- Class hierarchies, interface implementations, method signatures
- Cross-repo dependencies via HTTP calls, message bus events, and shared contracts

### 2. Natural Language Documentation (CODEGRAPH.md)

Generated markdown documents committed directly to each repository:

```
repo-root/
├── CODEGRAPH.md          # Repo-level summary: what this service is, what it does,
│                         #   what it depends on, what depends on it
├── src/
│   ├── TC.OrdersApi/
│   │   ├── CODEGRAPH.md  # Project-level: endpoints, services, DI, detailed behavior
│   │   └── ...
│   ├── TC.OrdersApi.Models/
│   │   ├── CODEGRAPH.md  # Public contracts: events, DTOs, shared models
│   │   └── ...
│   ├── TC.OrdersApi.Services/
│   │   ├── CODEGRAPH.md  # Business logic: what each service does and why
│   │   └── ...
│   ├── TC.OrdersApi.Data/
│   │   ├── CODEGRAPH.md  # Data access: which databases/tables, query patterns
│   │   └── ...
│   └── TC.OrdersApiJobs/
│       ├── CODEGRAPH.md  # Background jobs: what they do, what they trigger
│       └── ...
```

Each document includes a **confidence indicator** — high for clean, well-named services with clear intent; low for cryptic legacy code where Claude can describe the structure but not confidently explain the business purpose. "I don't know" is an acceptable and expected output for some repos.

### 3. Archival Candidates

A natural byproduct of the graph. Any repo with no inbound dependencies (nothing calls it, nothing consumes its events, nothing references its NuGet packages) and no outbound dependencies is an immediate candidate for archival. This is a primary early use case.

---

## The Questions CodeGraph Must Answer

These are the real queries people will ask Claude via the MCP server:

- **Data lineage**: "Where does this data come from? What repo is responsible for collecting and serving this model? What database and table does it originate from?"
- **Dependency impact**: "What other services consume this model? What would break if we changed it?"
- **Service discovery**: "What service handles X? What endpoint do I call to accomplish Y?"
- **Event tracing**: "What consumers consume these events? What queues are involved?"
- **Cross-service flow**: "How does an order get processed from the frontend all the way to the database?"
- **Service description**: "What does this service do and what relies on it?"
- **Archival triage**: "What repos have no dependencies and can be archived?"
- **Staleness**: "What repos haven't been touched in years?"

For most queries, the graph and CODEGRAPH.md summaries provide the answer. For deeper questions ("what validation happens before an order is submitted?"), Claude reads the actual source code from GitLab on demand.

---

## Repository Landscape

### Scale and Languages

- ~620 repositories in GitLab
- Primarily C#, Angular, and ColdFusion
- Some Python and miscellaneous experiments
- Everything under `svn_archive` group is deprioritized

### Standard C# Repository Structure

Most repos follow a consistent convention:

```
TC.RepoNameApi.sln
├── TC.RepoNameApi/           # API host — controllers, startup, DI registration
├── TC.RepoNameApi.Models/    # Public contracts — DTOs, events, shared models
│                             #   Published as NuGet package for other repos to reference
├── TC.RepoNameApi.Services/  # Business logic layer
├── TC.RepoNameApi.Data/      # Data access — EF/Dapper, database queries
└── TC.RepoNameApiJobs/       # Background jobs — registered in external scheduler
```

The `TC.RepoNameApi.Models` project is critical — it's the public contract surface. Published as a NuGet package, it's how repos share event definitions, request/response DTOs, and interface contracts. The qualified type name (e.g., `TC.OrdersApi.Models.OrderCreatedEvent`) is the canonical linking key across the entire system.

### Foundational / Framework Repositories

A set of ~12+ shared infrastructure repos including `TC.Common.ServiceStack` that provide:

- In-house MassTransit abstraction (ServiceBus class, queue routing attributes)
- Base classes and conventions that all other repos follow
- Shared attributes whose meaning must be understood to correctly interpret application repos

**These must be analyzed first.** They are the Rosetta Stone — without understanding what `[PublishToQueue("orders.created")]` means, Claude would see it as just another attribute. The indexing pipeline has a bootstrap order: foundational repos first, then everything else.

---

## Communication Channels Between Services

### 1. HTTP REST APIs

Direct service-to-service HTTP calls. Controllers define routes, other services call them via `HttpClient`. Cross-repo linking matches URL patterns to route definitions.

### 2. RabbitMQ via MassTransit (with in-house abstraction)

The in-house `ServiceBus` class wraps MassTransit. The pattern:

- **Publishing**: Code calls `serviceBus.Publish(someEvent)`. The event class is a POCO decorated with attributes that determine which queue/exchange it targets.
- **Event classes**: Plain C# classes in the `TC.*.Models` NuGet packages, decorated with routing attributes from the foundational framework.
- **Consumers**: Generic consumer classes — `Consumer<OrderCreatedEvent>`. The generic type parameter identifies what event is consumed. Attributes on the event determine which queues to listen on.
- **Registration**: Consumers are registered in startup.

The shared Models NuGet package is the linking key: the publisher and consumer reference the same event type by qualified name, even though they're in completely different repos.

### 3. Shared NuGet Packages (TC.*.Models)

Every repo's Models project is published as a NuGet package. Other repos reference it to use the public contracts. NuGet dependency analysis reveals which repos depend on which contracts.

### 4. Scheduled Jobs (External Scheduler)

Jobs live in `TC.RepoNameApiJobs` projects. They are registered in a **database table** in an external in-house job scheduler with cron schedules — not in code. The code defines the job classes; the scheduler database determines if and when they run.

If read access to the scheduler database is available, the graph can pull in active jobs and their schedules. Without it, the graph still captures the job classes and what they call, just not the schedule or active/inactive state.

---

## Indexing Pipeline

### Bootstrap Order

1. **Foundational repos first** — `TC.Common.ServiceStack` and other shared framework repos. Build understanding of in-house conventions, attributes, base classes.
2. **Application repos** — All remaining repos, using foundational knowledge to correctly interpret patterns.
3. **Cross-repo linking pass** — Wire up dependencies via shared NuGet type names, HTTP route matching, queue/exchange names, and consumer registrations.

### What Gets Extracted

#### Structural (Roslyn, TypeScript Compiler API, ScriptDom, Regex)

- Namespaces, classes, interfaces, structs, records, enums
- Methods, properties, constructors with signatures
- Inheritance and interface implementations
- DI registrations (AddScoped/AddTransient/AddSingleton)
- HTTP route definitions (controller attributes, minimal API)
- RabbitMQ publishers (ServiceBus.Publish calls + event type)
- RabbitMQ consumers (Consumer<T> registrations + event type)
- Event class definitions with queue routing attributes
- Database queries (EF, Dapper → table/proc references)
- NuGet package references (especially TC.*.Models)
- Import/using directives
- Call graphs (who calls what)

#### Semantic (Claude analysis)

Roslyn gives you structure. Claude gives you intent. For each repo and each project within it, Claude reads the actual code and produces:

- Plain-language description of what the service does in business terms
- Description of each public endpoint — not just the route, but what it accomplishes
- Explanation of business logic in the Services layer
- Description of data flow — where data enters, how it's transformed, where it goes
- Identification of external integrations (AWS services, partner APIs, GoDaddy, etc.)
- Confidence indicator for each summary

This is what populates the CODEGRAPH.md files.

### Incremental Updates (CI Integration)

On every push to a repository:

1. CI triggers the CodeGraph update pipeline
2. Extractors re-run on changed files, graph is updated
3. Claude analyzes the changes (code diff + commit messages) and determines if CODEGRAPH.md docs need revision
4. Updated docs are committed directly back to the repo — no MR, no human approval
5. Cross-repo link recalculation if public contracts changed

### Auto-Discovery

The system periodically scans GitLab groups for new repositories. When one appears:

1. Clone and full analysis (foundational knowledge already loaded)
2. Generate CODEGRAPH.md files
3. Commit docs to the repo
4. Add to the graph
5. Run cross-repo linking to connect it to existing services

Repos removed from GitLab are detected and cleaned from the graph.

---

## Technical Implementation

### Graph Storage (MySQL)

Adjacency tables in MySQL. No graph database — recursive CTEs handle traversal.

#### Node Types

| Label | Description |
|-------|-------------|
| Project | A repository |
| Namespace | C# namespace / TS module path |
| File, Folder | File system structure |
| Class, Interface, Struct, Record, Enum | Type definitions |
| Method, Function, Property, Constructor | Members |
| Delegate | C# delegate types |
| Route | HTTP endpoint (controller action / minimal API) |
| Service | DI-registered service |
| Table, View, StoredProcedure | SQL objects |
| Component, Module | Angular constructs |
| Event | Message bus event class |
| Queue, Exchange | RabbitMQ destinations |
| Job | Scheduled background job |

#### Edge Types

| Type | Description |
|------|-------------|
| CONTAINS_FILE, CONTAINS_FOLDER, CONTAINS_NAMESPACE | Structural containment |
| DEFINES, DEFINES_METHOD | Namespace/File → Type, Type → Member |
| CALLS | Method → Method (direct invocation) |
| IMPORTS | File → File/Namespace (using directives) |
| IMPLEMENTS | Class → Interface |
| INHERITS | Class → Class (base class) |
| USES_TYPE | Method → Class/Interface (parameter/return type) |
| INJECTS | Constructor → Interface (DI injection) |
| HTTP_CALLS | HttpClient call → Route (cross-repo) |
| HANDLES | Route → Method (controller action) |
| QUERIES | Method → Table/View/StoredProcedure |
| PUBLISHES | Method/Service → Event → Queue/Exchange |
| CONSUMES | Consumer → Event ← Queue |
| REFERENCES_PACKAGE | Project → NuGet package |
| RENDERS | Angular Component → Component (template) |
| SUBSCRIBES | Angular Component → Service (observable) |
| FILE_CHANGES_WITH | File → File (git co-change coupling) |
| SCHEDULES | Job scheduler → Job class |

### Extractors

| Extractor | Technology | Targets |
|-----------|-----------|---------|
| C# | Roslyn (SemanticModel via MSBuildWorkspace) | `.cs` — full semantic analysis including resolved types, overloads, generics, DI |
| TypeScript/Angular | Node.js sidecar using TypeScript compiler API | `.ts`, `.tsx`, `.html` templates — components, services, HTTP calls, imports |
| SQL | Microsoft.SqlServer.TransactSql.ScriptDom | `.sql` — tables, views, stored procedures, query relationships |
| ColdFusion | Regex (best effort) | `.cfm`, `.cfc` — functions, components, invocations, HTTP calls |

Roslyn loads full `.sln`/`.csproj` for semantic resolution — it doesn't just parse files in isolation. This gives resolved type names, overload resolution, generic instantiation, extension method resolution, and DI type argument resolution.

### MCP Server

Stdio transport, 12+ tools for IDE integration. Claude uses these tools to query the graph and read documentation:

- `search_graph` — Find services, endpoints, models by name/pattern
- `trace_call_path` — Follow calls/dependencies upstream or downstream
- `trace_data_lineage` — Follow a model from database through services to consumers
- `find_consumers` — What consumes this event/endpoint/model?
- `find_publishers` — What publishes to this queue/exchange?
- `get_service_summary` — Natural language description of a service (from CODEGRAPH.md)
- `get_architecture` — Architecture overview, hotspots, dependency analysis
- `find_archival_candidates` — Repos with no inbound or outbound dependencies
- `list_projects` — All indexed repos with metadata and staleness
- `get_code_snippet` — Read actual source from GitLab when deeper detail needed
- `index_repository` — Trigger manual re-index
- `get_graph_schema` — Describe available node/edge types

### REST API

For tooling, dashboards, and non-MCP consumers. Same query capabilities as MCP tools.

### GitLab Integration

- Repository discovery via GitLab REST API v4 (scan configured groups)
- Clone/pull via LibGit2Sharp
- Webhook receiver for push-triggered re-indexing
- File content access for on-demand source reading
- Maintainer metadata extraction (with caveat that many maintainers are just the IT coordinator)

---

## Configuration

```json
{
  "ConnectionStrings": {
    "CodeGraph": "Server=...;Database=codegraph;...",
    "JobScheduler": "Server=...;Database=scheduler;... (optional, read-only)"
  },
  "GitLab": {
    "BaseUrl": "https://gitlab.yourcompany.com",
    "AccessToken": "<PAT with read_api + read_repository + write_repository>",
    "Groups": ["*"],
    "DeprioritizedGroups": ["svn_archive"],
    "ClonePath": "/var/codegraph/repos",
    "SyncIntervalMinutes": 30
  },
  "Indexing": {
    "FoundationalRepos": [
      "TC.Common.ServiceStack",
      "... other framework repos"
    ],
    "MaxParallelRepos": 4,
    "MaxParallelFiles": 8,
    "SkipPatterns": ["**/bin/**", "**/obj/**", "**/node_modules/**",
                     "**/wwwroot/lib/**", "**/*.min.js"]
  },
  "Claude": {
    "ApiKey": "...",
    "Model": "claude-sonnet-4-6",
    "MaxTokensPerAnalysis": 8192
  }
}
```

Note: `write_repository` permission on the GitLab token is required for committing CODEGRAPH.md files back to repos.

---

## Key NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | Roslyn C# semantic analysis |
| `Microsoft.Build.Locator` | MSBuild resolution for Roslyn |
| `Microsoft.SqlServer.TransactSql.ScriptDom` | T-SQL parsing |
| `ModelContextProtocol` | .NET MCP SDK |
| `Dapper` | MySQL data access (not EF Core — graph queries are hand-written SQL) |
| `MySqlConnector` | Async MySQL driver |
| `LibGit2Sharp` | Git clone/pull/log |
| `Serilog` | Structured logging |

---

## Phased Delivery

### Phase 1 — Foundation + C# Graph

- Domain model (nodes, edges, new types for events/queues/jobs)
- MySQL storage with Dapper
- Roslyn extractor (classes, methods, calls, DI, MassTransit patterns, NuGet refs)
- Index foundational repos first, then a handful of application repos
- MCP server with core query tools
- CLI for manual testing

**Milestone**: Ask Claude "what calls GetWalletBalance?" and get a cross-service answer.

### Phase 2 — Claude Analysis + Documentation

- Claude-powered analysis pipeline for CODEGRAPH.md generation
- Confidence indicators
- Commit docs back to repos via GitLab API
- Data lineage tracing through the graph

**Milestone**: Every indexed repo has generated documentation with business-level descriptions.

### Phase 3 — Full GitLab Integration + CI

- Auto-discovery of all repos from GitLab groups
- CI webhook integration for incremental graph + doc updates
- Direct commit workflow for doc updates
- Stale repo detection
- Archival candidate identification

**Milestone**: System runs unattended. New repos are automatically analyzed. Pushes trigger updates.

### Phase 4 — TypeScript/Angular + SQL + ColdFusion

- TypeScript extractor (Node.js sidecar)
- SQL extractor (ScriptDom)
- ColdFusion extractor (regex, best effort)
- Full-stack tracing from Angular → API → database

**Milestone**: Complete cross-language knowledge graph.

### Phase 5 — Job Scheduler + Polish

- Job scheduler database integration (if access granted)
- Cross-service event flow visualization
- Architecture analysis (hotspots, coupling, dead code)
- Dashboard UI (optional)

**Milestone**: Complete knowledge graph covering all communication channels.

---

## Key Design Decisions

1. **Self-maintaining above all** — Every feature must work without human intervention in steady state.
2. **Two-layer analysis** — Roslyn/extractors for structure, Claude for intent. Both are necessary; neither alone is sufficient.
3. **Foundational repos analyzed first** — In-house abstractions must be understood before application code can be correctly interpreted.
4. **CODEGRAPH.md in the repos** — Documentation lives next to code, versioned in git, visible in merge requests.
5. **Direct commits, not MRs** — MRs add friction and would be ignored. Doc updates go straight in.
6. **Confidence indicators** — "I don't know" is better than a confident wrong answer. Legacy repos will have low-confidence summaries.
7. **Dapper over EF Core** — Graph queries are hand-written SQL with recursive CTEs.
8. **MySQL over graph DB** — Company already runs MySQL. Recursive CTEs handle graph traversal.
9. **Node.js sidecar for TypeScript** — TypeScript compiler API understands Angular natively.
10. **Shared NuGet qualified names as linking keys** — `TC.OrdersApi.Models.OrderCreatedEvent` is the canonical identifier that connects publishers to consumers across repos.
11. **On-demand source reading** — Graph and docs answer most questions. For deep dives, Claude reads source from GitLab at query time.
