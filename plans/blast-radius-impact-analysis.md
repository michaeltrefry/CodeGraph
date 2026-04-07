# Blast Radius / Change Impact Analysis

## Idea

Given a changed file, class, or method, traverse the graph outward to find everything affected — then classify each affected node by severity based on hop distance and node type. Answers the question: "if I change this, what could break?"

This is one of the highest-value features a knowledge graph can provide. The graph already has the edges; this is just a formalized traversal + presentation layer.

## What It Would Reveal

- **Direct dependents** (1 hop) — classes that call this method, consumers of this event
- **Transitive dependents** (2-3 hops) — services that depend on those dependents
- **Cross-repo blast** — other repos affected via HTTP calls, MassTransit events, NuGet packages
- **Risk classification** — CRITICAL / HIGH / MEDIUM / LOW based on distance + node importance
- **Scope estimation** — "this change affects 3 repos, 12 classes, 2 message consumers"

## Why This Fits CodeGraph Well

We already have:
- Inbound/outbound edge traversal (TraverseAsync with depth + edge type filters)
- Cross-repo edges (HTTP_CALLS, PUBLISHES/CONSUMES, REFERENCES_PACKAGE)
- Health metrics per file (churn, coupling, truck factor)
- Node analysis from Claude (knows business importance)

Blast radius is the natural next step — combine traversal with health/importance data to produce actionable impact reports.

## Risk Classification

| Level | Criteria | Example |
|-------|----------|---------|
| **CRITICAL** | Hop 0 (the changed node itself) + any directly coupled cross-repo consumer | Changed method, event consumer in another repo |
| **HIGH** | Hop 1 — direct callers/dependents | Class that calls the changed method |
| **MEDIUM** | Hop 2 — transitive dependents | Service that depends on a direct dependent |
| **LOW** | Hop 3+ — distant transitive impact | Repo that consumes an event from a hop-2 service |

### Boosting / Demoting Factors

- **Cross-repo edge at any hop** — boost severity by one level (cross-repo breakage is harder to detect)
- **Event/queue edge** — boost (async failures are silent)
- **Test node** — demote (test breakage is caught by CI)
- **High health score node** — demote (well-maintained code is more resilient)
- **High churn node** — boost (frequently changing code is fragile)
- **Low truck factor** — boost (fewer people understand it)

## Implementation Sketch

### Core: `IImpactAnalysisService`

```
AnalyzeImpactAsync(string qualifiedName, int maxDepth = 3)
  → ImpactReport { ChangedNode, AffectedNodes[], CrossRepoImpact[], Summary }

AnalyzeFileImpactAsync(string projectName, string filePath, int maxDepth = 3)
  → ImpactReport (resolves all nodes in file, unions their blast radii)

AnalyzeCommitImpactAsync(string projectName, string commitSha)
  → ImpactReport (resolves changed files from git diff, unions blast radii)
```

### Algorithm

1. Resolve the changed node(s) by qualified name or file path
2. BFS outward following CALLS, HTTP_CALLS, CONSUMES, REFERENCES_PACKAGE edges (inbound direction — "who depends on me")
3. At each hop, record distance and edge type
4. After traversal, classify each affected node using distance + boosting factors
5. Group by project for cross-repo summary
6. Optionally enrich with health metrics and Claude analysis summaries

### Storage

No new tables needed — this is a query-time computation. Could optionally cache results:

```sql
CREATE TABLE impact_snapshots (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    source_qualified_name VARCHAR(1000) NOT NULL,
    source_project VARCHAR(500) NOT NULL,
    affected_count INT NOT NULL,
    cross_repo_count INT NOT NULL,
    critical_count INT NOT NULL,
    high_count INT NOT NULL,
    medium_count INT NOT NULL,
    low_count INT NOT NULL,
    result_json JSON,
    computed_at DATETIME NOT NULL,
    INDEX idx_source (source_qualified_name)
);
```

### API / MCP Surface

- `analyze_impact` MCP tool — "what's the blast radius of changing OrderService.CreateOrder?"
- `GET /api/projects/{name}/impact?node={qn}&depth=3` — API endpoint
- UI: click a node in the graph, see its blast radius highlighted with color-coded severity

### Integration with Existing Features

- **Health metrics** — use churn/coupling/truck factor as severity modifiers
- **Cross-repo linker** — follow cross-repo edges during traversal
- **Claude analysis** — include business context in the report ("this affects the payment processing pipeline")
- **Ask in chat** — "what's the impact of changing the Order model?" triggers impact analysis tool

## Potential Uses

- **PR review** — automated comment: "this PR affects 3 downstream repos"
- **Migration planning** — before changing a shared NuGet model, see full blast radius
- **Incident response** — "this service is broken, what else might be affected?"
- **Refactoring confidence** — "this method only has 2 callers, both in the same repo, safe to change"

## Open Questions

- Should the depth limit be configurable per edge type? (e.g., follow CALLS 3 deep but HTTP_CALLS only 1 deep)
- How to handle circular dependencies in traversal? (already handled by BFS visited set, but worth noting in results)
- Should we pre-compute blast radius for high-fan-in nodes and cache it?
