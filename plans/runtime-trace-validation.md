# Runtime Trace Validation (Edge Confidence from Production Data)

## Idea

Use production runtime data to validate or enrich the statically-extracted graph edges. Static analysis says "Service A *can* call Service B." Runtime data says "Service A *actually does* call Service B, 4,200 times per day."

This closes the gap between what the code *could* do and what it *actually* does — dead routes, unused event consumers, and phantom dependencies become visible.

## Skepticism Acknowledged

This is the highest-friction idea of the four. Logging and tracing live in separate systems, and adding OpenTelemetry ingestion is a real dependency. This doc explores **multiple approaches**, starting with the lowest-cost options that might already work with existing infrastructure.

## What It Would Reveal

- **Validated edges** — "this HTTP_CALLS edge is confirmed by production traffic"
- **Dead edges** — "this CONSUMES edge has zero matching runtime events" (dead consumer?)
- **Missing edges** — "production shows traffic between A and B but the graph has no edge" (dynamic routing? config-driven?)
- **Edge weight from reality** — call frequency, error rates, latency (if available)
- **Confidence upgrade** — static analysis edges start at medium confidence; runtime confirmation bumps to high

## Approach Options (Least to Most Effort)

### Option A: Log Aggregation Query (Lowest Effort)

If HTTP access logs exist in a queryable system (ELK, Splunk, Loki, etc.):

1. Query: group by (source_service, target_service, http_method, path) over last 30 days
2. Match results against HTTP_CALLS cross-repo edges
3. Mark matched edges as runtime-validated; flag unmatched edges as potentially dead

**Requirements:** Read access to log aggregation. No new dependencies in CodeGraph itself — just a scheduled job that queries an external API and updates edge metadata.

**Pros:** Zero new infrastructure. Uses what already exists.
**Cons:** Only covers HTTP. Matching log entries to graph edges requires path normalization. Log retention limits historical depth.

### Option B: RabbitMQ Management API (Low Effort)

RabbitMQ's management API exposes queue/exchange statistics:

- `GET /api/queues` — message rates, consumer counts, idle time
- `GET /api/exchanges` — publish rates per exchange
- `GET /api/bindings` — exchange-to-queue bindings

A scheduled job could:
1. Fetch queue stats from RabbitMQ management API
2. Match queue names to CONSUMES edges in the graph
3. Flag queues with zero message rate as potentially dead
4. Flag queues with no consumers as orphaned

**Requirements:** RabbitMQ management plugin enabled (usually is), network access from CodeGraph.
**Pros:** Covers the messaging layer that HTTP logs miss. Very low effort.
**Cons:** Only aggregate stats, no per-message tracing. Queue names must map to graph edges (may need convention-based matching).

### Option C: Database Query Log Sampling (Medium Effort)

If MySQL slow query log or general query log is accessible:

1. Sample queries to identify which services hit which tables
2. Match against QUERIES edges in the graph
3. Validate or discover service-to-database relationships

**Requirements:** Query log access (often restricted). Sampling to avoid volume issues.

### Option D: OpenTelemetry Ingestion (Highest Effort, Richest Data)

Full OTLP span ingestion like codebase-memory-mcp implements.

1. Services emit OTLP traces (requires instrumentation if not already present)
2. CodeGraph ingests spans via OTLP HTTP receiver or reads from trace storage
3. Extract: source service, target service, HTTP method/path, status, latency
4. Create VALIDATED_BY edges or update confidence on existing edges

**Requirements:** OpenTelemetry instrumentation across services, trace collection infrastructure, OTLP endpoint or trace storage access.
**Pros:** The richest data — per-request detail, latency, error rates, full call chains.
**Cons:** Significant infrastructure dependency. If tracing isn't already deployed, this is a project in itself.

### Option E: Hybrid / Read from Existing Trace Storage (Medium Effort)

If traces already flow to a backend (Jaeger, Tempo, Zipkin, etc.) but aren't OTLP-accessible:

1. Query the trace backend's API on a schedule
2. Aggregate service-to-service edges from span data
3. Update graph edge confidence accordingly

**Requirements:** API access to trace backend. Understanding of service naming conventions in traces.

## Recommended Starting Point

**Options A + B together.** They require no new infrastructure, cover both HTTP and messaging layers, and deliver 80% of the value. The data model below supports upgrading to richer sources later.

## Data Model

```sql
CREATE TABLE edge_runtime_evidence (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    edge_id BIGINT,                           -- FK to edges table (nullable for discovered-only)
    cross_repo_edge_id BIGINT,                -- FK to cross_repo_edges (nullable)
    source_project VARCHAR(500) NOT NULL,
    target_project VARCHAR(500) NOT NULL,
    edge_type VARCHAR(100) NOT NULL,           -- HTTP_CALLS, CONSUMES, QUERIES
    evidence_source VARCHAR(100) NOT NULL,     -- 'http_logs', 'rabbitmq_mgmt', 'otel', etc.
    request_count BIGINT DEFAULT 0,
    error_count BIGINT DEFAULT 0,
    last_seen_at DATETIME,
    first_seen_at DATETIME,
    period_days INT NOT NULL,                  -- observation window
    metadata_json JSON,                        -- source-specific extras (avg latency, queue depth, etc.)
    computed_at DATETIME NOT NULL,
    INDEX idx_edge (edge_id),
    INDEX idx_cross_repo (cross_repo_edge_id),
    INDEX idx_projects (source_project, target_project)
);
```

### Edge Confidence Update Logic

| Static Confidence | Runtime Evidence | New Confidence |
|---|---|---|
| Any | Validated (request_count > 0) | HIGH |
| Medium/High | No evidence after 30 days | Downgrade to LOW (flag for review) |
| None (no static edge) | Evidence exists | Create DISCOVERED edge (runtime-only) |

## MCP / API Surface

- `get_edge_confidence` — show runtime validation status for a service's edges
- Enhance `get_project_health` — include "X% of edges runtime-validated"
- Enhance `get_architecture` — color edges by confidence (validated vs static-only vs dead)

## Open Questions

- What log/trace systems are currently in use and accessible? This determines which option is viable.
- Are RabbitMQ queue names deterministic from the graph? (MassTransit conventions suggest yes)
- How to handle environments? (Validate against prod only, or stage too?)
- How stale is "stale"? A consumer that processes one message per month isn't dead.
