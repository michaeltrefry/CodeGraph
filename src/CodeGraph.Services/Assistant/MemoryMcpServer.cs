using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Messaging;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Memory;

namespace CodeGraph.Services.Assistant;

[McpServerToolType]
public class MemoryMcpServer
{
    [McpServerTool(Name = "store_memory_v2", Title = "Store Memory V2", ReadOnly = false, Destructive = false)]
    [Description("""
        Store claim-centric memory in the Neo4j memory graph.
        Pass structured JSON with optional "entities", required atomic "claims", and optional "evidence".

        Entity format: { "id": "michael", "label": "Michael", "type": "person", "canonicalName": "Michael", "aliases": ["mike"] }
        Claim format: { "subject": "michael", "predicate": "prefers", "valueText": "clean slate design", "confidence": 0.9 }
        Evidence format: { "claimId": "optional-claim-id", "evidenceType": "conversation", "sourceRef": "thread-123", "snippet": "..." }
        """)]
    public static async Task<string> StoreMemoryV2(
        [Description("JSON object with 'entities', 'claims', and optional 'evidence' arrays")] string data,
        [Description("Source identifier (e.g. 'claude_conversation', 'document')")] string source,
        IMessageBus messageBus,
        ILogger<MemoryMcpServer> logger)
    {
        MemoryClaimExtractionResult extraction;
        try
        {
            extraction = JsonSerializer.Deserialize<MemoryClaimExtractionResult>(data)
                         ?? throw new JsonException("Deserialized to null");

            if (extraction.Entities.Count == 0 && extraction.Claims.Count == 0 && extraction.Evidence.Count == 0)
                return "Error: No entities, claims, or evidence provided.";
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse claim-centric structured input");
            return $"Error: Invalid JSON. Expected {{\"entities\": [...], \"claims\": [...], \"evidence\": [...]}}. Details: {ex.Message}";
        }

        await messageBus.PublishAsync(new StoreMemoryClaims
        {
            Extraction = extraction,
            Source = source,
        });

        return $"Memory v2 storage initiated — storing {extraction.Entities.Count} entities, {extraction.Claims.Count} claims, and {extraction.Evidence.Count} evidence rows.";
    }

    [McpServerTool(Name = "query_memory", Title = "Query Memory", ReadOnly = true)]
    [Description("Search the memory graph for information about a topic. Returns relevant entities, relationships, and any unresolved conflicts.")]
    public static async Task<string> QueryMemory(
        [Description("The topic to search for")] string topic,
        MemoryService memoryService,
        [Description("How many relationship hops to traverse (default 2)")] int hops = 2,
        [Description("Maximum number of entities to return (default 20)")] int maxNodes = 20)
    {
        hops = Math.Clamp(hops, 1, 5);
        maxNodes = Math.Clamp(maxNodes, 1, 50);

        var result = await memoryService.QueryAsync(topic, hops, maxNodes);
        return result.FormattedText;
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

        return JsonSerializer.Serialize(result);
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
        return JsonSerializer.Serialize(result);
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

        return bundle == null ? "Error: Entity not found." : JsonSerializer.Serialize(bundle);
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

        return bundle == null ? "Error: Claim not found." : JsonSerializer.Serialize(bundle);
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

        return JsonSerializer.Serialize(result);
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
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "migrate_memory_observations", Title = "Migrate Memory Observations", ReadOnly = false, Destructive = false)]
    [Description("Convert memory observations to claim-native ABOUT links while preserving entity links.")]
    public static async Task<string> MigrateMemoryObservations(MemoryService memoryService)
    {
        var result = await memoryService.MigrateObservationsAsync();
        return JsonSerializer.Serialize(result);
    }

    private static List<string> ParseCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
