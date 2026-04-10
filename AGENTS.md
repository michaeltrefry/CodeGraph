# AGENTS.md

This file provides guidance to Codex when working with code in this repository.

## Project Overview

CodeGraph is a self-maintaining .NET 9 service that indexes source repositories into a queryable knowledge graph (Neo4j) with natural language documentation. It produces two outputs: a structural graph of code and service relationships, and generated CODEGRAPH.md files committed to each repo describing business intent. An MCP server lets Codex act as the domain expert for the indexed codebase.

Use this file and the repository README as the current architecture guide. The older `CodeGraph-Architecture.md` and `CodeGraph-Implementation.md` references are no longer authoritative in this checkout.

## Core Design Principle

**Self-maintaining.** If it requires human attention to stay accurate, it will rot. Every feature must work without human intervention in steady state — auto-discovery of new repos, CI-triggered updates, direct commits for doc changes, automatic cleanup of removed repos.

## Build & Run Commands

```bash
dotnet build CodeGraph.sln                              # Build entire solution
dotnet run --project src/CodeGraph.Api                  # API host (REST + MCP)
dotnet test CodeGraph.sln                               # All tests
dotnet test src/CodeGraph.Tests                         # Specific test project
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

## Architecture

### Solution Structure

```
CodeGraph/
├── src/
│   ├── CodeGraph.Api/                   # API host (Startup.cs + Controllers + MCP), DI registration
│   ├── CodeGraph.Models/                # Domain model: GraphNode, GraphEdge, enums, contracts
│   ├── CodeGraph.Services/              # Pipeline, query engine, AI analysis, MCP tools,
│   │                                          #   ICodeExtractor interface, cross-repo linker
│   ├── CodeGraph.Data/                  # IGraphStore and store interfaces
│   ├── CodeGraph.Data.Neo4j/            # Neo4j implementations (Neo4jGraphStore, Cypher)
│   ├── CodeGraph.Jobs/                  # Background jobs and scheduled re-indexing
│   ├── CodeGraph.Extractors.CSharp/     # Roslyn extractor (isolated heavy dependency)
│   ├── CodeGraph.Extractors.TypeScript/ # Node.js sidecar (Phase 6+)
│   ├── CodeGraph.Extractors.Sql/        # ScriptDom (Phase 6+)
│   └── CodeGraph.Extractors.TreeSitter/ # Multi-language fallback extractor
├── CodeGraphWeb/                        # Angular frontend (port 4200)
└── src/CodeGraph.Api/Migrations/
```

### Dependency Flow

```
Models ← Data ← Services ← Extractors.*
                          ← Api (hosts everything)
                          ← Jobs
```

No references flow upward. Models has zero dependencies. Extractors depend only on Models and Services (for ICodeExtractor interface).

### Key Projects

- **CodeGraph.Models** — Graph model: `GraphNode`, `GraphEdge`, node/edge type enums, `ExtractionResult`, pipeline types. No dependencies.
- **CodeGraph.Data** — Store interfaces (`IGraphStore`, `IWikiStore`, etc.) and shared entities. No database dependency.
- **CodeGraph.Data.Neo4j** — Neo4j implementations of all store interfaces via the Neo4j .NET driver and Cypher queries.
- **CodeGraph.Services** — Pipeline orchestrator, `GraphBuffer`, `ICodeExtractor` interface, query engine, AI analysis, CODEGRAPH.md generation, MCP server tools, cross-repo linker. Bootstrap order: foundational repos first, then application repos, then cross-repo linking.
- **CodeGraph.Extractors.CSharp** — Roslyn `SemanticModel` via `MSBuildWorkspace`. Extracts types, calls, DI, MassTransit patterns, NuGet refs.
- **CodeGraph.Api** — ASP.NET Web API host. `Startup.cs` with controllers and DI registration. Hosts the MCP server (HTTP transport).
- **CodeGraph.Jobs** — `RepositorySyncWorker`, scheduled re-indexing tasks.

### Core Interfaces

- `ICodeExtractor` (in Services) — Language extractors implement this. Pipeline dispatches by file extension.
- `IGraphStore` (in Data) — Storage abstraction. Implemented by `Neo4jGraphStore` in Data.Neo4j.
- `IRepoProvider` (in Services) — Repository discovery and local materialization via folder, GitHub, or GitLab sources.

## MCP-First Discovery Policy

Default operating procedure for any agent working in this repo:

- Use CodeGraph MCP tools first for discovery, architecture questions, dependency tracing, and locating implementation.
- Start with graph and convention queries before opening source files directly.
- Memory tools are part of the broader CodeGraph MCP suite, not a separate `memorygraph` tool family.
- Prefer these CodeGraph MCP tools when they fit the question: `search_graph`, `get_service_summary`, `trace_call_path`, `trace_data_lineage`, `find_consumers`, `find_publishers`, `get_architecture`, `get_project_health`, `get_fleet_health`, `list_conventions`, `get_convention`, `get_code_snippet`, and `read_node_source`.
- Do not assume CodeGraph MCP results reflect the current git working tree or non-`main` branches; the indexed graph reflects committed code on `main`, not uncommitted local edits.
- Inspect source files directly when MCP results are insufficient, exact line-level behavior matters, the question depends on uncommitted or branch-only changes, or a file is about to be edited.
- Avoid broad file-reading sweeps when CodeGraph MCP can narrow the search space first.

## Memory System

The personal memory system in this repo is claim-centric and Neo4j-native.

Current memory MCP tools live under the main CodeGraph MCP surface. Use the current tool names such as `query_memory`, `search_memory`, `get_memory_subgraph`, `get_entity_bundle`, `get_claim_bundle`, `expand_memory_frontier`, `render_memory_summary`, `store_memory_v2`, `migrate_legacy_memory_graph`, and `migrate_memory_observations`. Do not refer to the retired `mcp__memorygraph__...` namespace in new docs or agent guidance.

### Memory Model

- `MemoryEntity` nodes are stable anchors for named things such as people, projects, tools, concepts, and decisions.
- `MemoryClaim` nodes are the primary truth unit. Atomic facts live in claims, not in entity summaries.
- `MemoryObservation` nodes represent unresolved contradiction or ambiguity.
- `MemoryEvidence` nodes capture provenance for claims and observations.
- Direct entity-to-entity memory edges are derived convenience edges only. They are not the source of truth.

### Working Rules

- Do not design new memory features around entity-summary accumulation.
- Do not treat human-readable markdown summaries as the primary memory retrieval contract.
- Prefer explicit claim status such as active, superseded, conflicted, and deprecated over implicit recency heuristics.
- Keep recency local to fact groups. Do not globally rank the memory graph by freshness alone.
- When exact behavior matters, inspect the current source. Memory tools and indexed results may lag local edits.

### Retrieval Expectations

- The primary read path should return structured memory subgraphs, not only rendered prose.
- Retrieval should combine exact recall, lexical recall, and vector recall before graph expansion.
- Iterative deepening is the expected access pattern: find seeds, fetch a bounded subgraph, inspect promising entities or claims, then expand only if needed.
- Human-readable summaries should be rendered from structured retrieval results as a secondary convenience layer.

### Migration Guidance

- The older memory implementation stored truth too coarsely in entity summaries and append-only relationship edges.
- When modifying memory code, prefer the claim-centric model even if temporary compatibility wrappers still exist.
- Do not attempt to recover precise atomic claims from already-merged entity summary text. Treat that text as descriptive metadata only.

## Legacy Codebase Conventions

Many of the originally indexed repos followed a consistent C# structure:

```
TC.RepoNameApi.sln
├── TC.RepoNameApi/           # API host, controllers, startup, DI
├── TC.RepoNameApi.Models/    # Public contracts — published as NuGet package
├── TC.RepoNameApi.Services/  # Business logic
├── TC.RepoNameApi.Data/      # Data access (EF/Dapper)
└── TC.RepoNameJobs/          # Background jobs (external scheduler)
```

Legacy `TC.*.Models` NuGet packages are still important linking keys when indexing that ecosystem, but newer repositories do not need to follow that naming convention.

### Four Communication Channels

1. **HTTP REST** — Controllers define routes, other services call via HttpClient
2. **RabbitMQ/MassTransit** — In-house `ServiceBus` abstraction. Events are POCOs with queue-routing attributes. Consumers are `Consumer<EventType>`. Registered in startup.
3. **Shared NuGet packages** — Shared contracts can connect publishers and consumers across repositories
4. **Scheduled jobs** — Job classes in code, registered externally in a scheduler database with cron schedules

### Foundational Repos

Framework repositories can be analyzed first when the indexed ecosystem depends on shared abstractions such as service bus wrappers, queue attributes, or base classes.

## Graph Model

### Key Node Types

Project, Namespace, File, Class, Interface, Method, Route, Service, Event, Queue, Exchange, Job, Table, View, StoredProcedure, Component (Angular)

### Key Edge Types

CALLS, HTTP_CALLS, PUBLISHES, CONSUMES, INJECTS, IMPLEMENTS, INHERITS, QUERIES, REFERENCES_PACKAGE, HANDLES, FILE_CHANGES_WITH

## Conventions Wiki

Database-backed wiki for team conventions and standards (patterns, abstractions, coding standards). Managed through the Angular UI and served to Codex via MCP tools.

- **DB tables**: `convention_pages` (current content + revision counter), `convention_revisions` (full snapshot per edit)
- **API**: `ConventionsController` — CRUD at `/api/conventions/{slug}`, revision history at `/api/conventions/{slug}/revisions`
- **MCP tools**: `list_conventions` and `get_convention` query the database (not the filesystem)
- **UI**: Angular pages at `/conventions` (list), `/conventions/new` (create), `/conventions/:slug` (view/edit/history)

## Angular Frontend (CodeGraphWeb/)

Standalone Angular app at `CodeGraphWeb/` served on port 4200. Uses signals, lazy-loaded routes, and calls the API at `localhost:5037`.

Key pages: Repositories, Graph (d3 visualization), Ask (streaming AI chat), Conventions (wiki).

## CODEGRAPH.md Generation

Codex analyzes each repo's code and generates natural language summaries with **confidence indicators** (high/medium/low). These are committed directly to repos — no MRs. Updated via CI when code changes, using diffs and commit messages to determine if revision is needed.

## Design Decisions

- **Neo4j graph database** — Native graph storage with Cypher queries for traversal and pattern matching
- **Roslyn for C#** — Semantic analysis far exceeds tree-sitter's syntactic parsing
- **Node.js sidecar for TypeScript** — TypeScript compiler API understands Angular natively
- **Direct commits, not MRs** — MRs add friction and would be ignored
- **Confidence indicators** — "I don't know" is better than a confident wrong answer
- **Shared NuGet qualified names as linking keys** — Canonical cross-repo identifiers
- **On-demand source reading** — Graph and docs answer most questions; Codex reads source from the local repo checkout for deep dives

## Key NuGet Packages

- `Microsoft.CodeAnalysis.CSharp.Workspaces` + `Microsoft.Build.Locator` — Roslyn
- `Microsoft.SqlServer.TransactSql.ScriptDom` — T-SQL parsing
- `ModelContextProtocol` — .NET MCP SDK
- `Neo4j.Driver` — Neo4j .NET driver
- `LibGit2Sharp` — Git operations
- `Microsoft.Extensions.Logging` — Logging (ILogger<T>)
- `Autofac` — Dependency injection container
