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
    [McpServerTool(Name = "store_memory", Title = "Store Memory", ReadOnly = false, Destructive = false)]
    [Description("""
        Store entities and relationships in the memory graph.
        Pass structured JSON with "nodes" and "edges" arrays.

        Node format: { "id": "snake_case_id", "label": "Display Name", "type": "person|project|concept|tool|fact|decision|codebase|component", "summary": "..." }
        Edge format: { "from": "node_id", "to": "node_id", "relationship": "verb_phrase", "context": "...", "conflicts": false }

        Set "conflicts": true on an edge if it contradicts previously stored knowledge.
        """)]
    public static async Task<string> StoreMemory(
        [Description("JSON object with 'nodes' and 'edges' arrays")] string data,
        [Description("Source identifier (e.g. 'claude_conversation', 'document')")] string source,
        IMessageBus messageBus,
        ILogger<MemoryMcpServer> logger)
    {
        MemoryExtractionResult extraction;
        try
        {
            extraction = JsonSerializer.Deserialize<MemoryExtractionResult>(data)
                         ?? throw new JsonException("Deserialized to null");

            if (extraction.Nodes.Count == 0 && extraction.Edges.Count == 0)
                return "Error: No nodes or edges provided.";
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse structured input");
            return $"Error: Invalid JSON. Expected {{\"nodes\": [...], \"edges\": [...]}}. Details: {ex.Message}";
        }

        await messageBus.PublishAsync(new StoreMemory
        {
            Extraction = extraction,
            Source = source,
        });

        return $"Memory storage initiated — storing {extraction.Nodes.Count} nodes and {extraction.Edges.Count} edges.";
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
}
