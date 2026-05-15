using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;

namespace CodeGraph.Services.Assistant;

/// <summary>
/// Consolidated intent-family MCP tools. Each tool routes by an explicit <c>operation</c>
/// argument to the existing native <see cref="CodeGraphMcpServer"/> / <see cref="MemoryMcpServer"/>
/// implementations and wraps the result in a stable JSON envelope. These are advertised
/// additively alongside the legacy narrow tools; legacy tools can be hidden via hub catalog
/// policy once parity is validated.
/// </summary>
[McpServerToolType]
public sealed class ConsolidatedMcpServer
{
    public const string FormatVersion = "1";

    private static readonly JsonSerializerOptions EnvelopeOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "codegraph_search", Title = "Search Code Graph", ReadOnly = true)]
    [Description("Search the code knowledge graph. operation=search finds nodes by name pattern; operation=schema describes node/edge types.")]
    public static async Task<string> CodegraphSearch(
        CodeGraphMcpServer codegraph,
        [Description("Operation: search (default) | schema.")] string operation = "search",
        [Description("Name pattern to search for (supports % wildcards). Required for operation=search.")] string? query = null,
        [Description("Filter by node type: Class, Method, Route, Service, Event, Queue, Table, Interface, etc.")] string? label = null,
        [Description("Filter by project/repository name.")] string? project = null,
        [Description("Max results (default 20).")] int limit = 20)
    {
        const string tool = "codegraph_search";
        switch (Normalize(operation))
        {
            case "" or "search":
                if (string.IsNullOrWhiteSpace(query))
                    return Error(tool, "search", "missing_argument", "operation=search requires 'query'.");
                return Wrap(tool, "search", await codegraph.SearchGraph(query, label, project, limit));
            case "schema":
                return Wrap(tool, "schema", codegraph.GetGraphSchema());
            default:
                return InvalidOperation(tool, operation, "search", "schema");
        }
    }

    [McpServerTool(Name = "rag_search", Title = "Convention Semantic Search", ReadOnly = true)]
    [Description("Semantic (RAG) search over company convention documents using hybrid BM25 + dense retrieval.")]
    public static async Task<string> RagSearch(
        CodeGraphMcpServer codegraph,
        [Description("Operation: search (default).")] string operation = "search",
        [Description("Natural-language query for the convention content.")] string? query = null,
        [Description("Maximum number of chunks to return (default 10).")] int topK = 10)
    {
        const string tool = "rag_search";
        switch (Normalize(operation))
        {
            case "" or "search":
                if (string.IsNullOrWhiteSpace(query))
                    return Error(tool, "search", "missing_argument", "operation=search requires 'query'.");
                return Wrap(tool, "search", await codegraph.SearchConventionsAsync(query, topK));
            default:
                return InvalidOperation(tool, operation, "search");
        }
    }

    [McpServerTool(Name = "graph_trace", Title = "Trace Graph Relationships", ReadOnly = true)]
    [Description("Trace relationships through the graph. operation=call_path | data_lineage | consumers | publishers | impact.")]
    public static async Task<string> GraphTrace(
        CodeGraphMcpServer codegraph,
        [Description("Operation: call_path | data_lineage | consumers | publishers | impact.")] string operation,
        [Description("The function, method, model, event, queue, or element name to trace.")] string? target = null,
        [Description("call_path direction: inbound | outbound | both (default both).")] string direction = "both",
        [Description("Traversal depth for call_path/impact (default 3).")] int depth = 3,
        [Description("Filter by project/repository name.")] string? project = null)
    {
        const string tool = "graph_trace";
        var op = Normalize(operation);
        if (op is "call_path" or "data_lineage" or "consumers" or "publishers" or "impact"
            && string.IsNullOrWhiteSpace(target))
            return Error(tool, op, "missing_argument", $"operation={op} requires 'target'.");

        return op switch
        {
            "call_path" => Wrap(tool, op, await codegraph.TraceCallPath(target!, direction, depth, project)),
            "data_lineage" => Wrap(tool, op, await codegraph.TraceDataLineage(target!, project)),
            "consumers" => Wrap(tool, op, await codegraph.FindConsumers(target!, project)),
            "publishers" => Wrap(tool, op, await codegraph.FindPublishers(target!, project)),
            "impact" => Wrap(tool, op, await codegraph.AnalyzeImpact(target!, project, depth)),
            _ => InvalidOperation(tool, operation, "call_path", "data_lineage", "consumers", "publishers", "impact"),
        };
    }

    [McpServerTool(Name = "graph_source", Title = "Read Graph Source", ReadOnly = true)]
    [Description("Read source code. operation=snippet reads a file line range; operation=node reads a graph node's source.")]
    public static async Task<string> GraphSource(
        CodeGraphMcpServer codegraph,
        [Description("Operation: snippet | node.")] string operation,
        [Description("Project/repository name. Required for operation=snippet.")] string? project = null,
        [Description("File path relative to repo root. Required for operation=snippet.")] string? filePath = null,
        [Description("Start line (0 for beginning).")] int startLine = 0,
        [Description("End line (0 for entire file).")] int endLine = 0,
        [Description("Graph node id. Required for operation=node.")] long? nodeId = null)
    {
        const string tool = "graph_source";
        switch (Normalize(operation))
        {
            case "snippet":
                if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(filePath))
                    return Error(tool, "snippet", "missing_argument", "operation=snippet requires 'project' and 'filePath'.");
                return Wrap(tool, "snippet", await codegraph.GetCodeSnippet(project, filePath, startLine, endLine));
            case "node":
                if (nodeId is null)
                    return Error(tool, "node", "missing_argument", "operation=node requires 'nodeId'.");
                return Wrap(tool, "node", await codegraph.ReadNodeSource(nodeId.Value));
            default:
                return InvalidOperation(tool, operation, "snippet", "node");
        }
    }

    [McpServerTool(Name = "project_report", Title = "Project Intelligence", ReadOnly = true)]
    [Description("Project/repository intelligence. operation=search | summary | architecture | health | fleet_health | archival_candidates.")]
    public static async Task<string> ProjectReport(
        CodeGraphMcpServer codegraph,
        [Description("Operation: search (default) | summary | architecture | health | fleet_health | archival_candidates.")] string operation = "search",
        [Description("Project/repository name. Required for summary, architecture, and health.")] string? project = null,
        [Description("Partial or wildcard repository name search (operation=search).")] string? search = null,
        [Description("Source group filter (operation=search).")] string? group = null,
        [Description("Page number, 1-based (operation=search).")] int page = 1,
        [Description("Page size (operation=search).")] int pageSize = 25,
        [Description("Number of top hotspot files to return (operation=health, default 10).")] int topHotspots = 10,
        [Description("Only show repos with health below this threshold (operation=fleet_health).")] double? maxHealth = null,
        [Description("Max repos to return (operation=fleet_health, default 50).")] int limit = 50)
    {
        const string tool = "project_report";
        var op = Normalize(operation);
        if (op is "summary" or "architecture" or "health" && string.IsNullOrWhiteSpace(project))
            return Error(tool, op, "missing_argument", $"operation={op} requires 'project'.");

        return op switch
        {
            "" or "search" => Wrap(tool, "search", await codegraph.SearchProjects(search, group, page, pageSize)),
            "summary" => Wrap(tool, op, await codegraph.GetServiceSummary(project!)),
            "architecture" => Wrap(tool, op, await codegraph.GetArchitecture(project!)),
            "health" => Wrap(tool, op, await codegraph.GetProjectHealth(project!, topHotspots)),
            "fleet_health" => Wrap(tool, op, await codegraph.GetFleetHealth(maxHealth, limit)),
            "archival_candidates" => Wrap(tool, op, await codegraph.FindArchivalCandidates()),
            _ => InvalidOperation(tool, operation, "search", "summary", "architecture", "health", "fleet_health", "archival_candidates"),
        };
    }

    [McpServerTool(Name = "graph_cluster", Title = "Service Clusters", ReadOnly = true)]
    [Description("Service cluster analysis. operation=list shows all clusters; operation=detail inspects one cluster.")]
    public static async Task<string> GraphCluster(
        CodeGraphMcpServer codegraph,
        [Description("Operation: list (default) | detail.")] string operation = "list",
        [Description("Cluster id. Required for operation=detail.")] int? clusterId = null)
    {
        const string tool = "graph_cluster";
        switch (Normalize(operation))
        {
            case "" or "list":
                return Wrap(tool, "list", await codegraph.GetServiceClusters());
            case "detail":
                if (clusterId is null)
                    return Error(tool, "detail", "missing_argument", "operation=detail requires 'clusterId'.");
                return Wrap(tool, "detail", await codegraph.GetClusterDetail(clusterId.Value));
            default:
                return InvalidOperation(tool, operation, "list", "detail");
        }
    }

    [McpServerTool(Name = "storage_table", Title = "Database Schema Catalog", ReadOnly = true)]
    [Description("Indexed database schema catalog. operation=list_schemas lists schema projects; operation=catalog returns one schema's full catalog.")]
    public static async Task<string> StorageTable(
        CodeGraphMcpServer codegraph,
        [Description("Operation: list_schemas (default) | catalog.")] string operation = "list_schemas",
        [Description("Search across schema project, server, and database names (operation=list_schemas).")] string? search = null,
        [Description("Server name filter (operation=list_schemas).")] string? server = null,
        [Description("Database name filter (operation=list_schemas).")] string? database = null,
        [Description("Page number, 1-based (operation=list_schemas).")] int page = 1,
        [Description("Page size (operation=list_schemas).")] int pageSize = 25,
        [Description("Schema project name, for example db:server/database. Required for operation=catalog.")] string? name = null)
    {
        const string tool = "storage_table";
        switch (Normalize(operation))
        {
            case "" or "list_schemas":
                return Wrap(tool, "list_schemas", await codegraph.ListSchemas(search, server, database, page, pageSize));
            case "catalog":
                if (string.IsNullOrWhiteSpace(name))
                    return Error(tool, "catalog", "missing_argument", "operation=catalog requires 'name'.");
                return Wrap(tool, "catalog", await codegraph.GetSchemaCatalog(name));
            default:
                return InvalidOperation(tool, operation, "list_schemas", "catalog");
        }
    }

    [McpServerTool(Name = "convention", Title = "Convention Documents", ReadOnly = true)]
    [Description("Company convention documents. operation=list lists available documents; operation=get reads one by slug.")]
    public static async Task<string> Convention(
        CodeGraphMcpServer codegraph,
        [Description("Operation: list (default) | get.")] string operation = "list",
        [Description("Convention slug. Required for operation=get.")] string? name = null)
    {
        const string tool = "convention";
        switch (Normalize(operation))
        {
            case "" or "list":
                return Wrap(tool, "list", await codegraph.ListConventionsAsync());
            case "get":
                if (string.IsNullOrWhiteSpace(name))
                    return Error(tool, "get", "missing_argument", "operation=get requires 'name'.");
                return Wrap(tool, "get", await codegraph.GetConventionAsync(name));
            default:
                return InvalidOperation(tool, operation, "list", "get");
        }
    }

    [McpServerTool(Name = "memory_read", Title = "Read Memory Graph", ReadOnly = true)]
    [Description("Read the claim-centric memory graph. operation=query | search | subgraph | entity_bundle | claim_bundle | expand_frontier | render_summary.")]
    public static async Task<string> MemoryRead(
        IMemoryOperationsService memoryOperations,
        [Description("Operation: query | search | subgraph | entity_bundle | claim_bundle | expand_frontier | render_summary.")] string operation,
        [Description("Query/topic text (query, search, subgraph).")] string? query = null,
        [Description("Single entity id (entity_bundle).")] string? entityId = null,
        [Description("Single claim id (claim_bundle).")] string? claimId = null,
        [Description("Comma-separated entity ids: subgraph seeds, expand_frontier frontier, or render_summary targets.")] string? entityIds = null,
        [Description("Comma-separated claim ids: subgraph seeds, expand_frontier frontier, or render_summary targets.")] string? claimIds = null,
        [Description("Traversal hops for query/subgraph/expand_frontier (default 2).")] int hops = 2,
        [Description("Max entities/nodes to return. Defaults per operation when omitted.")] int? entityLimit = null,
        [Description("Max claims to return. Defaults per operation when omitted.")] int? claimLimit = null,
        [Description("Minimum score threshold (expand_frontier).")] double minScore = 0,
        [Description("Include superseded claims (subgraph, entity_bundle).")] bool includeSuperseded = false,
        [Description("Include conflicted claims (subgraph, entity_bundle, claim_bundle).")] bool includeConflicts = true,
        [Description("Include supersession chain (claim_bundle).")] bool includeSupersessionChain = true,
        [Description("Include evidence (claim_bundle).")] bool includeEvidence = true,
        [Description("Rendering style for render_summary: markdown | plain.")] string style = "markdown")
    {
        const string tool = "memory_read";
        var op = Normalize(operation);
        switch (op)
        {
            case "query":
                if (string.IsNullOrWhiteSpace(query))
                    return Error(tool, op, "missing_argument", "operation=query requires 'query'.");
                return Wrap(tool, op, await MemoryMcpServer.QueryMemory(query, memoryOperations, hops, entityLimit ?? 20));
            case "search":
                if (string.IsNullOrWhiteSpace(query))
                    return Error(tool, op, "missing_argument", "operation=search requires 'query'.");
                return Wrap(tool, op, await MemoryMcpServer.SearchMemory(query, memoryOperations, entityLimit ?? 5, claimLimit ?? 5));
            case "subgraph":
                return Wrap(tool, op, await MemoryMcpServer.GetMemorySubgraph(
                    query, entityIds, claimIds, memoryOperations,
                    hops, entityLimit ?? 20, claimLimit ?? 40, includeSuperseded, includeConflicts));
            case "entity_bundle":
                if (string.IsNullOrWhiteSpace(entityId))
                    return Error(tool, op, "missing_argument", "operation=entity_bundle requires 'entityId'.");
                return Wrap(tool, op, await MemoryMcpServer.GetEntityBundle(
                    entityId, memoryOperations, includeSuperseded, includeConflicts, entityLimit ?? 20));
            case "claim_bundle":
                if (string.IsNullOrWhiteSpace(claimId))
                    return Error(tool, op, "missing_argument", "operation=claim_bundle requires 'claimId'.");
                return Wrap(tool, op, await MemoryMcpServer.GetClaimBundle(
                    claimId, memoryOperations, includeSupersessionChain, includeConflicts, includeEvidence));
            case "expand_frontier":
                return Wrap(tool, op, await MemoryMcpServer.ExpandMemoryFrontier(
                    entityIds, claimIds, memoryOperations, hops, entityLimit ?? 20, minScore));
            case "render_summary":
                return Wrap(tool, op, await MemoryMcpServer.RenderMemorySummary(entityIds, claimIds, memoryOperations, style));
            default:
                return InvalidOperation(tool, operation,
                    "query", "search", "subgraph", "entity_bundle", "claim_bundle", "expand_frontier", "render_summary");
        }
    }

    [McpServerTool(Name = "memory_store", Title = "Store Memory", ReadOnly = false, Destructive = false)]
    [Description("Store claim-centric memory. operation=store queues entities, claims, and evidence into the memory graph.")]
    public static async Task<string> MemoryStore(
        IMemoryOperationsService memoryOperations,
        ILogger<MemoryMcpServer> logger,
        [Description("Operation: store (default).")] string operation = "store",
        [Description("Source identifier (e.g. 'claude_conversation', 'document').")] string source = "mcp",
        [Description("Legacy JSON object with 'entities', 'claims', and optional 'evidence' arrays.")] string? data = null,
        [Description("Typed memory entities to store.")] List<MemoryExtractedEntity>? entities = null,
        [Description("Typed atomic memory claims to store.")] List<MemoryExtractedClaim>? claims = null,
        [Description("Typed evidence rows to store.")] List<MemoryExtractedEvidence>? evidence = null)
    {
        const string tool = "memory_store";
        switch (Normalize(operation))
        {
            case "" or "store":
                return Wrap(tool, "store", await MemoryMcpServer.StoreMemoryV2(
                    memoryOperations, logger, source, data, entities, claims, evidence));
            default:
                return InvalidOperation(tool, operation, "store");
        }
    }

    [McpServerTool(Name = "memory_diagnostics", Title = "Memory Diagnostics", ReadOnly = true)]
    [Description("Memory operational diagnostics. operation=write_status returns the durable status of a queued memory write.")]
    public static async Task<string> MemoryDiagnostics(
        IMemoryOperationsService memoryOperations,
        [Description("Operation: write_status (default).")] string operation = "write_status",
        [Description("Memory write receipt id. Required for operation=write_status.")] string? receiptId = null)
    {
        const string tool = "memory_diagnostics";
        switch (Normalize(operation))
        {
            case "" or "write_status":
                if (string.IsNullOrWhiteSpace(receiptId))
                    return Error(tool, "write_status", "missing_argument", "operation=write_status requires 'receiptId'.");
                return Wrap(tool, "write_status", await MemoryMcpServer.GetMemoryWriteStatus(receiptId, memoryOperations));
            default:
                return InvalidOperation(tool, operation, "write_status");
        }
    }

    private static string Normalize(string? operation) =>
        operation?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// Wraps a legacy tool's output in the stable envelope. When the legacy output is a JSON
    /// object or array it is embedded as a structured node; otherwise (markdown / plain text)
    /// it is embedded verbatim as a string, keeping markdown output close to legacy.
    /// </summary>
    private static string Wrap(string tool, string operation, string legacyOutput)
    {
        var envelope = new JsonObject
        {
            ["tool"] = tool,
            ["operation"] = operation,
            ["formatVersion"] = FormatVersion,
            ["result"] = ToResultNode(legacyOutput),
        };
        return envelope.ToJsonString(EnvelopeOptions);
    }

    private static JsonNode? ToResultNode(string legacyOutput)
    {
        var trimmed = legacyOutput.AsSpan().TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            try
            {
                return JsonNode.Parse(legacyOutput);
            }
            catch (JsonException)
            {
                // Not actually JSON (e.g. markdown that happens to start with a bracket) —
                // fall through and embed as a string.
            }
        }

        return JsonValue.Create(legacyOutput);
    }

    private static string Error(string tool, string operation, string code, string message)
    {
        var envelope = new JsonObject
        {
            ["tool"] = tool,
            ["operation"] = operation,
            ["formatVersion"] = FormatVersion,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        return envelope.ToJsonString(EnvelopeOptions);
    }

    private static string InvalidOperation(string tool, string? operation, params string[] valid) =>
        Error(tool, operation?.Trim() ?? string.Empty, "invalid_operation",
            $"Unknown operation '{operation}'. Valid operations: {string.Join(", ", valid)}.");
}
