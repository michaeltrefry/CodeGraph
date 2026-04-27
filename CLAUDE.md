# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Project Overview

CodeGraph is a self-maintaining .NET 10 platform that indexes source repositories into a MariaDB-backed knowledge graph with natural language documentation. It produces a structural graph of code and service relationships, persisted analysis/review/memory data, and generated CODEGRAPH.md files committed to each repo describing business intent. An MCP server lets Claude act as the domain expert for the indexed codebase.

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
│   ├── CodeGraph.Data.MariaDb/          # MariaDB/MySQL runtime provider and SQL migrations
│   ├── CodeGraph.Data.Neo4j/            # Temporary compatibility/export provider
│   ├── CodeGraph.Jobs/                  # Background jobs and scheduled re-indexing
│   ├── CodeGraph.Extractors.Ansible/    # Ansible playbook/role extractor
│   ├── CodeGraph.Extractors.ColdFusion/ # ColdFusion CFM/CFC extractor
│   ├── CodeGraph.Extractors.CSharp/     # Roslyn extractor (isolated heavy dependency)
│   ├── CodeGraph.Extractors.TypeScript/ # Node.js sidecar
│   ├── CodeGraph.Extractors.Sql/        # ScriptDom extractor
│   ├── CodeGraph.Extractors.Terraform/  # Terraform/HCL extractor
│   └── CodeGraph.Extractors.TreeSitter/ # Multi-language fallback extractor
├── CodeGraphWeb/                        # Angular frontend (port 4200)
└── sql/migrations/                      # MariaDB schema and feature migrations
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
- **CodeGraph.Data.MariaDb** — MariaDB/MySQL runtime implementations of store interfaces, SQL migration runner, and EF Core mappings for graph, analysis, review, admin, assistant/MCP, metrics, jobs, wiki, vector, and memory persistence.
- **CodeGraph.Data.Neo4j** — Temporary Neo4j compatibility/export implementation retained during the standalone rebase. Do not treat it as the primary runtime backend unless a task explicitly asks for migration/export work.
- **CodeGraph.Services** — Pipeline orchestrator, `GraphBuffer`, `ICodeExtractor` interface, query engine, AI analysis, CODEGRAPH.md generation, MCP server tools, cross-repo linker. Bootstrap order: foundational repos first, then application repos, then cross-repo linking.
- **CodeGraph.Extractors.CSharp** — Roslyn `SemanticModel` via `MSBuildWorkspace`. Extracts types, calls, DI, MassTransit patterns, NuGet refs.
- **CodeGraph.Api** — ASP.NET Web API host. `Startup.cs` with controllers and DI registration. Hosts the MCP server (HTTP transport).
- **CodeGraph.Jobs** — `RepositorySyncWorker`, scheduled re-indexing tasks.

### Core Interfaces

- `ICodeExtractor` (in Services) — Language extractors implement this. Pipeline dispatches by file extension.
- `IGraphStore` (in Data) — Storage abstraction. Implemented for runtime by `MySqlGraphStore` in Data.MariaDb; Neo4j remains for compatibility/export.
- `IRepoProvider` (in Services) — Repository discovery and local materialization via folder, GitHub, or GitLab sources.

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
3. **Shared NuGet packages** — `TC.*.Models` contain public contracts (DTOs, events)
4. **Scheduled jobs** — Job classes in code, registered externally in a scheduler database with cron schedules

### Foundational Repos

`TC.Common.ServiceStack` and ~12 other framework repos define in-house abstractions (ServiceBus, queue attributes, base classes). **These must be analyzed first** — they're the Rosetta Stone for interpreting all other repos.

## Graph Model

### Key Node Types

Project, Namespace, File, Class, Interface, Method, Route, Service, Event, Queue, Exchange, Job, Table, View, StoredProcedure, Component (Angular)

### Key Edge Types

CALLS, HTTP_CALLS, PUBLISHES, CONSUMES, INJECTS, IMPLEMENTS, INHERITS, QUERIES, REFERENCES_PACKAGE, HANDLES, FILE_CHANGES_WITH

## Conventions Wiki

Database-backed wiki for conventions and standards (patterns, abstractions, coding standards). Managed through the Angular UI and served via MCP tools.

- **DB tables**: `convention_pages` (current content + revision counter), `convention_revisions` (full snapshot per edit)
- **API**: `ConventionsController` — CRUD at `/api/conventions/{slug}`, revision history at `/api/conventions/{slug}/revisions`
- **MCP tools**: `list_conventions` and `get_convention` query the database (not the filesystem)
- **UI**: Angular pages at `/conventions` (list), `/conventions/new` (create), `/conventions/:slug` (view/edit/history)

## Angular Frontend (CodeGraphWeb/)

Standalone Angular app at `CodeGraphWeb/` served on port 4200. Uses signals, lazy-loaded routes, and calls the API at `localhost:5037`.

Key pages: Repositories, Graph (d3 visualization), Ask (streaming AI chat), Conventions (wiki).

## CODEGRAPH.md Generation

The configured analysis model generates natural language summaries with **confidence indicators** (high/medium/low). These are committed directly to repos — no MRs. Updated via CI when code changes, using diffs and commit messages to determine if revision is needed.

## Design Decisions

- **MariaDB/MySQL primary persistence** — Runtime storage for graph, analysis, reviews, admin/settings, assistant/MCP telemetry, jobs, wiki, vectors, and memory
- **Neo4j compatibility boundary** — Retained temporarily for migration/export reference, not as an equal first-class runtime backend for the first standalone MariaDB release
- **Roslyn for C#** — Semantic analysis far exceeds tree-sitter's syntactic parsing
- **Node.js sidecar for TypeScript** — TypeScript compiler API understands Angular natively
- **Direct commits, not MRs** — MRs add friction and would be ignored
- **Confidence indicators** — "I don't know" is better than a confident wrong answer
- **Shared NuGet qualified names as linking keys** — Canonical cross-repo identifiers
- **On-demand source reading** — Graph and docs answer most questions; Claude reads source from the local repo checkout for deep dives

## Key NuGet Packages

- `Microsoft.CodeAnalysis.CSharp.Workspaces` + `Microsoft.Build.Locator` — Roslyn
- `Microsoft.SqlServer.TransactSql.ScriptDom` — T-SQL parsing
- `ModelContextProtocol` — .NET MCP SDK
- `Pomelo.EntityFrameworkCore.MySql` + `MySqlConnector` — MariaDB/MySQL runtime persistence
- `Neo4j.Driver` — Temporary compatibility/export provider
- `LibGit2Sharp` — Git operations
- `Microsoft.Extensions.Logging` — Logging (ILogger<T>)
- `Autofac` — Dependency injection container
