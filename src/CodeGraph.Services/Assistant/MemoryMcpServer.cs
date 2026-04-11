using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;

namespace CodeGraph.Services.Assistant;

[McpServerToolType]
public class MemoryMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [McpServerTool(Name = "store_memory_v2", Title = "Store Memory V2", ReadOnly = false, Destructive = false)]
    [Description("""
        Store claim-centric memory in the Neo4j memory graph.
        Prefer typed MCP arguments for entities, claims, and evidence. A legacy JSON payload is still accepted through "data" for compatibility.

        Entity format: { "id": "michael", "label": "Michael", "type": "person", "canonicalName": "Michael", "aliases": ["mike"] }
        Claim format: { "subject": "michael", "predicate": "prefers", "valueText": "clean slate design", "confidence": 0.9 }
        Evidence format: { "claimId": "optional-claim-id", "evidenceType": "conversation", "sourceRef": "thread-123", "snippet": "..." }
        """)]
    public static async Task<string> StoreMemoryV2(
        MemoryService memoryService,
        IMessageBus messageBus,
        ILogger<MemoryMcpServer> logger,
        [Description("Source identifier (e.g. 'claude_conversation', 'document')")] string source = "mcp",
        [Description("Legacy JSON object with 'entities', 'claims', and optional 'evidence' arrays")] string? data = null,
        [Description("Typed memory entities to store")] List<MemoryExtractedEntity>? entities = null,
        [Description("Typed atomic memory claims to store")] List<MemoryExtractedClaim>? claims = null,
        [Description("Typed evidence rows to store")] List<MemoryExtractedEvidence>? evidence = null)
    {
        var hasData = !string.IsNullOrWhiteSpace(data);
        var hasTypedInput = (entities?.Count ?? 0) > 0 || (claims?.Count ?? 0) > 0 || (evidence?.Count ?? 0) > 0;

        if (hasData && hasTypedInput)
            return SerializeError("invalid_input", "Provide either typed arguments or legacy JSON data, not both.");

        var extraction = TryBuildExtraction(data, entities, claims, evidence, logger, out var parseError);
        if (parseError != null)
            return SerializeError("invalid_json", parseError);

        if (extraction.Entities.Count == 0 && extraction.Claims.Count == 0 && extraction.Evidence.Count == 0)
            return SerializeError("empty_input", "No entities, claims, or evidence provided.");

        var ack = await memoryService.QueueClaimsAsync(
            extraction,
            source,
            hasData ? "json" : "typed",
            messageBus);

        return JsonSerializer.Serialize(ack, JsonOptions);
    }

    [McpServerTool(Name = "get_memory_write_status", Title = "Get Memory Write Status", ReadOnly = true)]
    [Description("Get the durable status of a queued memory write by receipt id.")]
    public static async Task<string> GetMemoryWriteStatus(
        [Description("Memory write receipt id")] string receiptId,
        MemoryService memoryService)
    {
        var receipt = await memoryService.GetWriteReceiptAsync(receiptId);
        return receipt == null
            ? SerializeError("not_found", $"Memory write receipt '{receiptId}' was not found.")
            : JsonSerializer.Serialize(receipt, JsonOptions);
    }

    [McpServerTool(Name = "query_memory", Title = "Query Memory", ReadOnly = true)]
    [Description("Search the memory graph for information about a topic. Returns structured JSON with entities, conflicts, the bounded subgraph, and a rendered summary.")]
    public static async Task<string> QueryMemory(
        [Description("The topic to search for")] string topic,
        MemoryService memoryService,
        [Description("How many relationship hops to traverse (default 2)")] int hops = 2,
        [Description("Maximum number of entities to return (default 20)")] int maxNodes = 20)
    {
        hops = Math.Clamp(hops, 1, 5);
        maxNodes = Math.Clamp(maxNodes, 1, 50);

        var result = await memoryService.QueryAsync(topic, hops, maxNodes);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "search_memory", Title = "Search Memory", ReadOnly = true)]
    [Description("Search claim-centric memory and return top entity and claim seeds as structured JSON.")]
    public static async Task<string> SearchMemory(
        [Description("The memory query text")] string query,
        MemoryService memoryService,
        [Description("Maximum entity seeds to return")] int entityLimit = 5,
        [Description("Maximum claim seeds to return")] int claimLimit = 5)
    {
        var result = await memoryService.SearchMemoryAsync(
            query,
            Math.Clamp(entityLimit, 1, 25),
            Math.Clamp(claimLimit, 1, 25));

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "get_memory_subgraph", Title = "Get Memory Subgraph", ReadOnly = true)]
    [Description("Fetch a bounded structured memory subgraph around seed entities and claims.")]
    public static async Task<string> GetMemorySubgraph(
        [Description("Optional query text used to discover seeds")] string? query,
        [Description("Comma-separated seed entity ids")] string? seedEntityIds,
        [Description("Comma-separated seed claim ids")] string? seedClaimIds,
        MemoryService memoryService,
        [Description("Maximum traversal hops")] int maxHops = 2,
        [Description("Maximum entities to return")] int maxReturnedEntities = 20,
        [Description("Maximum claims to return")] int maxReturnedClaims = 40,
        [Description("Include superseded claims")] bool includeSuperseded = false,
        [Description("Include conflicted claims")] bool includeConflicts = true)
    {
        var request = new MemorySubgraphRequest
        {
            Query = query,
            SeedEntityIds = ParseCsv(seedEntityIds),
            SeedClaimIds = ParseCsv(seedClaimIds),
            MaxHops = Math.Clamp(maxHops, 1, 5),
            MaxReturnedEntities = Math.Clamp(maxReturnedEntities, 1, 100),
            MaxReturnedClaims = Math.Clamp(maxReturnedClaims, 1, 200),
            IncludeSuperseded = includeSuperseded,
            IncludeConflicts = includeConflicts,
        };

        var result = await memoryService.GetMemorySubgraphAsync(request);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "get_entity_bundle", Title = "Get Entity Bundle", ReadOnly = true)]
    [Description("Inspect one memory entity deeply, including claims, neighbors, and observations.")]
    public static async Task<string> GetEntityBundle(
        [Description("The entity id")] string entityId,
        MemoryService memoryService,
        [Description("Include superseded claims")] bool includeSuperseded = false,
        [Description("Include conflicted claims")] bool includeConflicts = true,
        [Description("Maximum neighbor edges to return")] int neighborLimit = 20)
    {
        var bundle = await memoryService.GetEntityBundleAsync(
            entityId,
            includeSuperseded,
            includeConflicts,
            Math.Clamp(neighborLimit, 1, 100));

        return bundle == null
            ? SerializeError("not_found", $"Memory entity '{entityId}' was not found.")
            : JsonSerializer.Serialize(bundle, JsonOptions);
    }

    [McpServerTool(Name = "get_claim_bundle", Title = "Get Claim Bundle", ReadOnly = true)]
    [Description("Inspect one memory claim deeply, including fact-group peers, supersession, conflicts, and evidence.")]
    public static async Task<string> GetClaimBundle(
        [Description("The claim id")] string claimId,
        MemoryService memoryService,
        [Description("Include supersession chain")] bool includeSupersessionChain = true,
        [Description("Include conflicts")] bool includeConflicts = true,
        [Description("Include evidence")] bool includeEvidence = true)
    {
        var bundle = await memoryService.GetClaimBundleAsync(
            claimId,
            includeSupersessionChain,
            includeConflicts,
            includeEvidence);

        return bundle == null
            ? SerializeError("not_found", $"Memory claim '{claimId}' was not found.")
            : JsonSerializer.Serialize(bundle, JsonOptions);
    }

    [McpServerTool(Name = "expand_memory_frontier", Title = "Expand Memory Frontier", ReadOnly = true)]
    [Description("Perform iterative deepening from a known entity or claim frontier and return newly discovered nodes and paths.")]
    public static async Task<string> ExpandMemoryFrontier(
        [Description("Comma-separated frontier entity ids")] string? frontierEntityIds,
        [Description("Comma-separated frontier claim ids")] string? frontierClaimIds,
        MemoryService memoryService,
        [Description("Maximum additional hops to explore")] int maxAdditionalHops = 2,
        [Description("Maximum number of newly discovered nodes to return")] int frontierLimit = 20,
        [Description("Minimum score threshold for newly discovered nodes")] double minScore = 0)
    {
        var result = await memoryService.ExpandMemoryFrontierAsync(new MemoryFrontierExpansionRequest
        {
            FrontierEntityIds = ParseCsv(frontierEntityIds),
            FrontierClaimIds = ParseCsv(frontierClaimIds),
            MaxAdditionalHops = Math.Clamp(maxAdditionalHops, 1, 5),
            FrontierLimit = Math.Clamp(frontierLimit, 1, 100),
            MinScore = Math.Clamp(minScore, 0, 100),
        });

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "render_memory_summary", Title = "Render Memory Summary", ReadOnly = true)]
    [Description("Render a markdown or plain-text summary for known memory entities and claims.")]
    public static async Task<string> RenderMemorySummary(
        [Description("Comma-separated entity ids")] string? entityIds,
        [Description("Comma-separated claim ids")] string? claimIds,
        MemoryService memoryService,
        [Description("Rendering style: markdown or plain")] string style = "markdown")
    {
        var result = await memoryService.RenderMemorySummaryAsync(new MemorySummaryRenderRequest
        {
            EntityIds = ParseCsv(entityIds),
            ClaimIds = ParseCsv(claimIds),
            Style = style,
        });

        return result.Text;
    }

    [McpServerTool(Name = "migrate_legacy_memory_graph", Title = "Migrate Legacy Memory Graph", ReadOnly = false, Destructive = false)]
    [Description("Convert legacy RELATES_TO memory relationships into claim-centric memory records.")]
    public static async Task<string> MigrateLegacyMemoryGraph(MemoryService memoryService)
    {
        var result = await memoryService.MigrateLegacyRelationshipsAsync();
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "migrate_memory_observations", Title = "Migrate Memory Observations", ReadOnly = false, Destructive = false)]
    [Description("Convert memory observations to claim-native ABOUT links while preserving entity links.")]
    public static async Task<string> MigrateMemoryObservations(MemoryService memoryService)
    {
        var result = await memoryService.MigrateObservationsAsync();
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static List<string> ParseCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static MemoryClaimExtractionResult TryBuildExtraction(
        string? data,
        List<MemoryExtractedEntity>? entities,
        List<MemoryExtractedClaim>? claims,
        List<MemoryExtractedEvidence>? evidence,
        ILogger logger,
        out string? parseError)
    {
        parseError = null;

        if (!string.IsNullOrWhiteSpace(data))
        {
            try
            {
                return JsonSerializer.Deserialize<MemoryClaimExtractionResult>(data)
                       ?? throw new JsonException("Deserialized to null");
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse claim-centric structured input");
                parseError = $"Invalid JSON. Expected {{\"entities\": [...], \"claims\": [...], \"evidence\": [...]}}. Details: {ex.Message}";
                return new MemoryClaimExtractionResult();
            }
        }

        return new MemoryClaimExtractionResult
        {
            Entities = entities ?? [],
            Claims = claims ?? [],
            Evidence = evidence ?? [],
        };
    }

    private static string SerializeError(string code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            error = new
            {
                code,
                message,
            },
        }, JsonOptions);
    }
}
