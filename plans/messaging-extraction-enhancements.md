# Messaging Extraction Enhancements

**Status:** Planned
**Created:** 2026-03-20
**Context:** Identified during codebase review. The core PUBLISHES/CONSUMES extraction and cross-repo linking work well, but several messaging-related node types and metadata are defined but unused.

---

## 1. Populate Queue and Exchange Nodes

**Problem:** `NodeLabel.Queue` and `NodeLabel.Exchange` exist in the enum but nothing creates them. All messaging targets collapse into `Event` nodes, losing topology information.

**What to do:**
- In `CodeGraphSyntaxWalker`, when detecting `DetectServiceBusPublish` or `DetectConsumer`, also look for queue/exchange configuration:
  - `[TcServiceBusEvent]` attributes on event classes (extract queue name, exchange, virtual host)
  - MassTransit endpoint configuration in startup (e.g., `cfg.ReceiveEndpoint("queue-name", ...)`)
- Create `Queue` and `Exchange` nodes with properties: `queue_name`, `exchange_name`, `virtual_host`, `durable`, `auto_delete`
- Add edges: `Event --ROUTED_TO--> Queue`, `Queue --BOUND_TO--> Exchange` (may need new edge types)

**Key files:**
- `src/TC.CodeGraphApi.Extractors.CSharp/CodeGraphSyntaxWalker.cs` — extraction logic
- `src/TC.CodeGraphApi.Models/NodeLabel.cs` — already has Queue/Exchange
- `src/TC.CodeGraphApi.Models/EdgeType.cs` — may need new edge types

**Estimated scope:** Medium. Attribute extraction is straightforward; startup configuration parsing is harder since it's often lambda-based.

---

## 2. Extract Routing Attributes from TcServiceBus Events

**Problem:** The in-house `[TcServiceBusEvent]` attribute carries routing metadata (queue name, virtual host, custom queue routing) that we currently ignore.

**What to do:**
- In `CodeGraphSyntaxWalker`, when visiting class declarations for event types, check for `[TcServiceBusEvent]` and related attributes
- Extract attribute arguments: queue name, exchange, virtual host, routing key
- Store as properties on the Event node: `queue_name`, `exchange_name`, `virtual_host`, `routing_key`
- Also check for `[TcServiceBusCommand]` and similar attribute variants

**Key files:**
- `src/TC.CodeGraphApi.Extractors.CSharp/CodeGraphSyntaxWalker.cs` — add attribute inspection in `VisitClassDeclaration`
- Reference the foundational repo `TC.Common.ServiceStack` for the canonical attribute definitions

**Estimated scope:** Small-Medium. Attribute extraction via Roslyn is well-understood; the main work is cataloging which attributes exist across the foundational repos.

---

## 3. Index Event Message Contracts (Field-Level Extraction)

**Problem:** Events are treated as opaque types. Their properties (the actual message schema) aren't indexed, so we can't trace data flow through events.

**What to do:**
- When an Event node is created (or resolved from a stub), extract the public properties of the event class
- Store as structured metadata in the node's `properties` JSON: `fields: [{ name, type, nullable }]`
- This enables queries like "which services produce or consume OrderId?" by searching event field types
- Consider adding `CARRIES_FIELD` edges from Event to referenced model types if the field type is a known domain type

**Key files:**
- `src/TC.CodeGraphApi.Extractors.CSharp/CodeGraphSyntaxWalker.cs` — property extraction when visiting event classes
- `src/TC.CodeGraphApi.Services/Pipeline/IndexingPipeline.Resolution.cs` — enrich stub nodes when the real event class is later indexed

**Estimated scope:** Medium. Property extraction is simple; the interesting part is deciding how deep to go (nested types? collections of domain types?).

---

## 4. Extract Consumer DI Registration from Startup

**Problem:** `AddConsumers()` and explicit consumer registrations in Startup/DI aren't analyzed. No visibility into which consumers are actually wired up vs. just defined.

**What to do:**
- In the Roslyn extractor, detect consumer registration patterns in Startup/configuration classes:
  - `AddConsumer<T>()`, `AddConsumers(typeof(...).Assembly)`
  - MassTransit `cfg.ReceiveEndpoint(...)` with consumer configuration
  - In-house `ServiceBus.RegisterConsumer<T>()` patterns
- Create edges from the host Project node to Consumer classes: `REGISTERS` edge type
- Flag consumer classes that exist but aren't registered (dead code detection)

**Key files:**
- `src/TC.CodeGraphApi.Extractors.CSharp/CodeGraphSyntaxWalker.cs` — detect registration calls
- `src/TC.CodeGraphApi.Models/EdgeType.cs` — may need `REGISTERS` edge type

**Estimated scope:** Medium-Hard. Registration patterns vary significantly across repos and MassTransit versions. Start with the most common patterns and iterate.

---

## 5. Capture Consumer Configuration Metadata

**Problem:** Consumer concurrency limits, retry policies, and routing keys defined via MassTransit attributes or fluent configuration are not captured.

**What to do:**
- Extract MassTransit attributes on consumer classes: `[ConcurrencyLimit]`, `[RetryPolicy]`, etc.
- Extract fluent configuration in endpoint definitions: `e.PrefetchCount`, `e.ConcurrentMessageLimit`, `e.UseMessageRetry(...)`
- Store as properties on consumer Class nodes: `concurrency_limit`, `prefetch_count`, `retry_policy`
- This enables operational queries: "which consumers have no retry policy?" or "which consumers have high concurrency?"

**Key files:**
- `src/TC.CodeGraphApi.Extractors.CSharp/CodeGraphSyntaxWalker.cs` — attribute + fluent config extraction
- Consumer class nodes already exist; just need richer properties

**Estimated scope:** Small-Medium. Attribute extraction is easy; fluent configuration parsing is moderate.

---

## Implementation Order

Recommended sequence based on dependencies and value:

1. **Item 2** (routing attributes) — smallest scope, immediate value, no new node types needed
2. **Item 1** (Queue/Exchange nodes) — builds on item 2's attribute data to create topology
3. **Item 3** (event contracts) — independent of 1-2, high value for data-flow tracing
4. **Item 4** (DI registration) — enables dead-code detection, moderate complexity
5. **Item 5** (consumer config) — operational metadata, nice-to-have after core extraction works

Items 2-3 could be done in parallel since they're independent.

---

## New Edge Types Potentially Needed

| Edge Type | From | To | Purpose |
|-----------|------|----|---------|
| `ROUTED_TO` | Event | Queue | Event is routed to a specific queue |
| `BOUND_TO` | Queue | Exchange | Queue is bound to an exchange |
| `REGISTERS` | Project/Class | Consumer | Startup registers a consumer |
| `CARRIES_FIELD` | Event | Class/Type | Event contains a field of this domain type (optional) |

## Testing Strategy

- Extend `RoslynExtractorTests` (which already has `Detects_MassTransitConsumer`) with tests for each new extraction
- Create sample code fixtures with representative attribute patterns from the target repos
- Add integration tests for cross-repo linking with Queue/Exchange nodes