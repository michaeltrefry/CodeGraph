# Cross-Repo Community Detection (Louvain Clustering)

## Idea

Run Louvain community detection across the entire cross-repo graph to automatically discover **service clusters** — groups of repos that are tightly coupled and effectively function as a single system. This complements per-repo LLM-generated analysis with a deterministic, graph-math view of architecture.

## What It Would Reveal

- **De facto service boundaries** — "these 8 repos are really one system" based on edge density
- **Hidden dependency hubs** — repos that bridge multiple clusters (high betweenness centrality)
- **Isolation candidates** — loosely coupled clusters that could be deployed/owned independently
- **Coupling hotspots** — clusters with unexpectedly high cross-cluster edges (architectural smell)
- **Domain groupings** — repos that share events, HTTP calls, and NuGet packages cluster naturally by business domain

## How Louvain Works (Brief)

1. Start with each node (repo) in its own community
2. For each node, calculate modularity gain of moving it to each neighbor's community
3. Move the node to the community with the highest gain (if positive)
4. Repeat until no moves improve modularity
5. Collapse communities into super-nodes and repeat at the next level

**Modularity** measures: (edges within community) vs (expected edges if random). High modularity = real clusters, not noise.

## Input Graph

The cross-repo edge table already has what we need:

- **HTTP_CALLS** — Service A calls Service B's route
- **PUBLISHES/CONSUMES** — Service A publishes event, Service B consumes it
- **REFERENCES_PACKAGE** — Service A depends on Service B's NuGet models
- **DEPLOYS/CONFIGURES** — IaC links

Edge weights could factor in:
- **Count** — 20 HTTP calls between two repos = stronger link than 1
- **Type** — Event coupling (async) might be weighted differently than HTTP (sync)
- **Bidirectionality** — A calls B AND B calls A = much stronger coupling signal

## Implementation Sketch

### Option A: In-Application (C#)

Add a `CommunityDetectionService` that:
1. Queries all cross-repo edges from MySQL into an adjacency list
2. Runs Louvain in-memory (the algorithm is simple — ~200 lines of code)
3. Stores results in a new `repo_clusters` table (repo_id, cluster_id, level, modularity_score)
4. Exposes via API + MCP tool

The cross-repo graph is small (600 nodes, maybe 5K-10K edges) so this runs in milliseconds.

### Option B: As a Scheduled Job

Run after CrossRepoLinker completes (or on schedule). Re-cluster whenever the cross-repo edge set changes materially.

### Storage

```sql
CREATE TABLE repo_clusters (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project_name VARCHAR(500) NOT NULL,
    cluster_id INT NOT NULL,
    cluster_label VARCHAR(500),        -- optional: auto-generated or Claude-named
    modularity_score DECIMAL(6,4),
    level INT DEFAULT 0,               -- hierarchical: 0 = finest, 1+ = coarser
    computed_at DATETIME NOT NULL,
    INDEX idx_cluster (cluster_id),
    INDEX idx_project (project_name)
);
```

### MCP / API Surface

- `get_service_clusters` — list all clusters with member repos and edge counts
- `get_cluster_detail` — repos in a cluster, internal vs external edges, coupling metrics
- Enhance `get_architecture` to include cluster membership

### Bonus: LLM-Named Clusters

After Louvain identifies clusters, pass the member repos + their `CODEGRAPH.md` summaries to the configured analysis model and ask it to name the cluster (e.g., "Order Fulfillment Domain", "Authentication & Identity", "Shared Infrastructure"). Deterministic clustering plus semantic naming.

## Open Questions

- What edge weight scheme produces the most meaningful clusters? Needs experimentation.
- Should foundational/framework repos (TC.Common.*) be excluded or included? They'll likely form their own cluster or distort others.
- At what granularity? Repo-level clustering first, but could also cluster at namespace or class level within a repo.
- How to handle repos that straddle two clusters (high betweenness)? Flag them as boundary services.

## References

- Louvain algorithm: Blondel et al., 2008 — "Fast unfolding of communities in large networks"
- codebase-memory-mcp implements this at the function level within a single repo; we'd apply it at the repo level across the fleet
