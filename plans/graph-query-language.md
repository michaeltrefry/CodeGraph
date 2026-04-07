# Graph Query Language: Do We Need One?

## The Question

codebase-memory-mcp supports a Cypher-like query language:
```
MATCH (f:Function)-[:CALLS*1..3]->(g:Function)
WHERE f.name =~ ".*Order.*"
RETURN g.name, COUNT(*) AS cnt
ORDER BY cnt DESC
```

Is this useful for CodeGraph, or are the existing MCP tools sufficient?

## Argument Against (Current MCP Tools Are Probably Better for Agents)

The current MCP tools (`search_graph`, `trace_call_path`, `trace_data_lineage`, `find_consumers`, `find_publishers`, `get_architecture`) are **intent-based** — they encode common query patterns with sensible defaults. An agent calls `find_consumers("OrderCreatedEvent")` instead of writing:

```
MATCH (e:Event {name: "OrderCreatedEvent"})<-[:CONSUMES]-(c:Class)-[:CONTAINS*]-(p:Project)
RETURN p.name, c.name
```

**Why intent-based tools win for agents:**
- Lower token cost (tool name + 1 param vs multi-line query string)
- No syntax errors (structured parameters vs string parsing)
- Encodes domain knowledge (edge type selection, cross-repo joins, confidence filtering)
- LLM agents already know how to use MCP tools; Cypher is another thing to get right
- Our graph has domain-specific semantics (PUBLISHES, CONSUMES, HTTP_CALLS) that purpose-built tools handle better than generic traversal

**Verdict for agents: Don't bother.** The MCP tools are the right interface. Adding Cypher would give agents more ways to get the same answers, with more opportunities to write broken queries.

## Argument For (Power Users and Ad-Hoc Exploration)

Where a query language shines is **ad-hoc questions that no one anticipated**:

- "Find all classes that both publish events AND have HTTP routes" (intersection query)
- "Which repos have inbound edges from more than 5 other repos?" (aggregation)
- "Show me the shortest path between ServiceA and ServiceB" (pathfinding)
- "Find all nodes with no inbound edges except from their own project" (isolation detection)

These are valid questions but infrequent. The current answer is: ask via the Ask endpoint, and the model chains MCP tool calls to answer. That works but is slow and token-expensive for complex graph queries.

## Middle Ground: Structured Query Endpoint (Not Cypher)

Instead of implementing a query language parser, consider a **structured query API** that covers the gap:

### `POST /api/graph/query`

```json
{
  "match": {
    "nodeType": "Class",
    "project": "OrdersApi",
    "namePattern": ".*Service$"
  },
  "traverse": {
    "direction": "inbound",
    "edgeTypes": ["CALLS", "HTTP_CALLS"],
    "maxDepth": 2
  },
  "filter": {
    "crossRepoOnly": true
  },
  "aggregate": {
    "groupBy": "project",
    "count": true
  },
  "limit": 50
}
```

This gives power users composability without a parser. It's also trivially exposed as an MCP tool (`advanced_query`) for cases where the purpose-built tools don't fit.

### MCP Tool Version

```
advanced_graph_query(
  node_type: "Class",
  name_pattern: ".*Service$",
  project: "OrdersApi",
  traverse_direction: "inbound",
  edge_types: ["CALLS", "HTTP_CALLS"],
  max_depth: 2,
  cross_repo_only: true,
  group_by: "project"
)
```

Same capability, no string parsing, agent-friendly parameters.

## Recommendation

1. **Don't implement Cypher.** The parser is non-trivial, and the primary consumers (Claude agents) are better served by structured tools.
2. **Consider an `advanced_query` MCP tool** if the existing tools prove too rigid for real-world questions. Wait until there are concrete examples of questions the current tools can't answer efficiently.
3. **If a query language ever becomes necessary**, consider exposing a constrained read-only Neo4j/Cypher tool with a pre-built safety layer rather than inventing a second query language.

## The Neo4j Escape Hatch

Worth noting: the graph is in Neo4j. A constrained `run_graph_cypher` MCP tool that executes read-only traversals against a curated allowlist could be more useful than inventing a separate DSL:

```cypher
MATCH (source:Project)-[r]->(target:Project)
WHERE source <> target
RETURN target.name AS project, count(r) AS inboundDeps
ORDER BY inboundDeps DESC
LIMIT 10
```

The model is already good at writing Cypher. If ad-hoc traversal becomes a real need, this is likely the pragmatic answer.

## Open Questions

- Are there concrete questions that the current 17 MCP tools can't answer? Collect these before building anything.
- Would a read-only SQL view layer over the graph tables be sufficient for power users?
- Is the Angular UI the right place for ad-hoc queries, or is this purely an MCP/API concern?
