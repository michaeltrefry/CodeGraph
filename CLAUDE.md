# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CodeGraph is a self-maintaining .NET 9 service that indexes ~620 GitLab repositories into a queryable knowledge graph (MySQL) with natural language documentation. It produces two outputs: a structural graph of all connections between services, and generated CODEGRAPH.md files committed to each repo describing business intent. An MCP server lets Claude act as the domain expert for the entire codebase.

**The full architecture spec is in `CodeGraph-Architecture.md`. Read it before making structural decisions.**
**The detailed implementation plan is in `CodeGraph-Implementation.md`. Follow it for build order and code patterns.**

## Core Design Principle

**Self-maintaining.** If it requires human attention to stay accurate, it will rot. Every feature must work without human intervention in steady state — auto-discovery of new repos, CI-triggered updates, direct commits for doc changes, automatic cleanup of removed repos.

## Build & Run Commands

```bash
dotnet build src/TC.CodeGraphApi.sln                                    # Build entire solution
dotnet run --project src/TC.CodeGraphApi                                # API host (REST + sync worker)
dotnet run --project src/TC.CodeGraphApi.Console -- index /path/to/repo # Index a repo via CLI
dotnet run --project src/TC.CodeGraphApi.Console -- mcp                 # Start MCP server (stdio)
dotnet run --project src/TC.CodeGraphApi.Console -- migrate             # Apply DB migrations
dotnet test                                                             # All tests
dotnet test tests/TC.CodeGraphApi.Tests                                 # Specific test project
dotnet test --filter "FullyQualifiedName~TestMethodName"                 # Single test
```

## Architecture

### Solution Structure (follows company convention)

```
TC.CodeGraphApi/
├── src/
│   ├── TC.CodeGraphApi.sln
│   ├── TC.CodeGraphApi/                       # API host (Startup.cs + Controllers), DI registration
│   ├── TC.CodeGraphApi.Console/               # CLI: index, analyze, mcp, migrate, stats
│   ├── TC.CodeGraphApi.Models/                # Domain model: GraphNode, GraphEdge, enums, contracts
│   ├── TC.CodeGraphApi.Services/              # Pipeline, query engine, Claude analysis, MCP tools,
│   │                                          #   ICodeExtractor interface, cross-repo linker
│   ├── TC.CodeGraphApi.Data/                  # IGraphStore, MySqlGraphStore, Dapper, migrations
│   ├── TC.CodeGraphJobs/                   # Background sync worker, scheduled re-indexing
│   ├── TC.CodeGraphApi.Extractors.CSharp/     # Roslyn extractor (isolated heavy dependency)
│   ├── TC.CodeGraphApi.Extractors.TypeScript/ # Node.js sidecar (Phase 6+)
│   ├── TC.CodeGraphApi.Extractors.Sql/        # ScriptDom (Phase 6+)
│   └── TC.CodeGraphApi.Extractors.ColdFusion/ # Regex (Phase 6+)
├── tests/
└── sql/migrations/
```

### Dependency Flow

```
Models ← Data ← Services ← Extractors.*
                          ← Api (hosts everything)
                          ← Jobs
```

No references flow upward. Models has zero dependencies. Extractors depend only on Models and Services (for ICodeExtractor interface).

### Key Projects

- **TC.CodeGraphApi.Models** — Graph model: `GraphNode`, `GraphEdge`, node/edge type enums, `ExtractionResult`, pipeline types. No dependencies.
- **TC.CodeGraphApi.Data** — MySQL via **EF Core** (Pomelo) for CRUD + **Dapper** for graph traversal (recursive CTEs) and batch operations. `IGraphStore`, `MySqlGraphStore`, `CodeGraphDbContext`.
- **TC.CodeGraphApi.Services** — Pipeline orchestrator, `GraphBuffer`, `ICodeExtractor` interface, query engine, Claude analysis, CODEGRAPH.md generation, MCP server tools, cross-repo linker. Bootstrap order: foundational repos first, then application repos, then cross-repo linking.
- **TC.CodeGraphApi.Extractors.CSharp** — Roslyn `SemanticModel` via `MSBuildWorkspace`. Extracts types, calls, DI, MassTransit patterns, NuGet refs.
- **TC.CodeGraphApi** — ASP.NET WebApi host. `Startup.cs` with controllers, Autofac DI registration.
- **TC.CodeGraphApi.Console** — CLI commands: `index`, `index-all`, `analyze`, `mcp`, `migrate`, `stats`.
- **TC.CodeGraphJobs** — `RepositorySyncWorker`, scheduled re-indexing tasks.

### Core Interfaces

- `ICodeExtractor` (in Services) — Language extractors implement this. Pipeline dispatches by file extension.
- `IGraphStore` (in Data) — Storage abstraction (MySQL/Dapper).
- `IGitLabService` (in Services, Phase 6+) — Repository discovery, sync, change detection.

## Target Codebase Conventions

The ~620 repos follow a consistent C# structure:

```
TC.RepoNameApi.sln
├── TC.RepoNameApi/           # API host, controllers, startup, DI
├── TC.RepoNameApi.Models/    # Public contracts — published as NuGet package
├── TC.RepoNameApi.Services/  # Business logic
├── TC.RepoNameApi.Data/      # Data access (EF/Dapper)
└── TC.RepoNameJobs/          # Background jobs (external scheduler)
```

**`TC.*.Models` NuGet packages are the canonical linking key across repos.** The qualified type name (e.g., `TC.OrdersApi.Models.OrderCreatedEvent`) connects publishers to consumers across repos.

### Four Communication Channels

1. **HTTP REST** — Controllers define routes, other services call via HttpClient
2. **RabbitMQ/MassTransit** — In-house `ServiceBus` abstraction. Events are POCOs with queue-routing attributes. Consumers are `Consumer<EventType>`. Registered in startup.
3. **Shared NuGet packages** — `TC.*.Models` contain public contracts (DTOs, events)
4. **Scheduled jobs** — Job classes in code, registered externally in a scheduler database with cron schedules

### Foundational Repos

`TC.Common.ServiceStack` and ~12 other framework repos define in-house abstractions (ServiceBus, queue attributes, base classes). **These must be analyzed first** — they're the Rosetta Stone for interpreting all other repos.

## Graph Model

### Key Node Types

Project, Namespace, File, Class, Interface, Method, Route, Service, Event, Queue, Exchange, Job, Table, View, StoredProcedure, Component (Angular)

### Key Edge Types

CALLS, HTTP_CALLS, PUBLISHES, CONSUMES, INJECTS, IMPLEMENTS, INHERITS, QUERIES, REFERENCES_PACKAGE, HANDLES, FILE_CHANGES_WITH

## CODEGRAPH.md Generation

Claude analyzes each repo's code and generates natural language summaries with **confidence indicators** (high/medium/low). These are committed directly to repos — no MRs. Updated via CI when code changes, using diffs and commit messages to determine if revision is needed.

## Design Decisions

- **EF Core + Dapper hybrid** — EF Core for CRUD, Dapper for recursive CTEs and batch operations
- **MySQL over graph DB** — Company already runs MySQL; recursive CTEs handle traversal
- **Roslyn for C#** — Semantic analysis far exceeds tree-sitter's syntactic parsing
- **Node.js sidecar for TypeScript** — TypeScript compiler API understands Angular natively
- **Direct commits, not MRs** — MRs add friction and would be ignored
- **Confidence indicators** — "I don't know" is better than a confident wrong answer
- **Shared NuGet qualified names as linking keys** — Canonical cross-repo identifiers
- **On-demand source reading** — Graph and docs answer most questions; Claude reads source from GitLab for deep dives

## MySQL-Specific SQL Notes

- `ON DUPLICATE KEY UPDATE` (not `ON CONFLICT`)
- `JSON_MERGE_PATCH()` (not `json_patch()`)
- `LIKE CONCAT('%', ?)` (not `LIKE '%' || ?`)
- `BIGINT AUTO_INCREMENT PRIMARY KEY` (not `INTEGER PRIMARY KEY AUTOINCREMENT`)
- No bind variable limit (batch sizes can be larger than SQLite's 999)

## Key NuGet Packages

- `Microsoft.CodeAnalysis.CSharp.Workspaces` + `Microsoft.Build.Locator` — Roslyn
- `Microsoft.SqlServer.TransactSql.ScriptDom` — T-SQL parsing
- `ModelContextProtocol` — .NET MCP SDK
- `Dapper` + `MySqlConnector` — MySQL access
- `LibGit2Sharp` — Git operations
- `Microsoft.Extensions.Logging` — Logging (ILogger<T>)
- `Autofac` — Dependency injection container
