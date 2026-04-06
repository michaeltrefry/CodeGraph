using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services.Embeddings;

/// <summary>
/// Combines vector search with graph hydration for semantic code search.
/// Falls back gracefully when embeddings are not available.
/// </summary>
public class SemanticSearchService(
    IVectorStore vectorStore,
    IGraphStore graphStore,
    IEmbeddingService embeddingService,
    ILogger<SemanticSearchService> logger)
    : ISemanticSearchService
{
    public bool IsAvailable => embeddingService.IsAvailable;

    public async Task<IReadOnlyList<SemanticSearchResult>> SearchAsync(
        string query, string? project = null, int topK = 10)
    {
        if (!embeddingService.IsAvailable)
            return [];

        var queryEmbedding = embeddingService.GenerateEmbedding(query);
        var vectorResults = await vectorStore.SearchSimilarAsync(
            queryEmbedding, entityType: "CodeNode", topK: topK);

        if (vectorResults.Count == 0)
            return [];

        // Hydrate with full graph nodes
        var nodeIds = vectorResults
            .Where(r => long.TryParse(r.EntityKey, out _))
            .Select(r => long.Parse(r.EntityKey))
            .ToList();

        var nodes = await graphStore.FindNodesByIdBatchAsync(nodeIds);

        var results = new List<SemanticSearchResult>();
        foreach (var vr in vectorResults)
        {
            if (long.TryParse(vr.EntityKey, out var nodeId) && nodes.TryGetValue(nodeId, out var node))
            {
                if (project is not null && node.Project != project)
                    continue;

                results.Add(new SemanticSearchResult(node, vr.Score));
            }
        }

        return results;
    }

    public async Task IndexNodeAsync(GraphNode node, string? description = null)
    {
        if (!embeddingService.IsAvailable) return;

        var text = BuildNodeText(node, description);
        var embedding = embeddingService.GenerateEmbedding(text);
        await vectorStore.StoreEmbeddingAsync("CodeNode", node.Id.ToString(), embedding);
    }

    public async Task IndexNodeBatchAsync(IReadOnlyList<(GraphNode node, string? description)> items)
    {
        if (!embeddingService.IsAvailable || items.Count == 0) return;

        var texts = items.Select(i => BuildNodeText(i.node, i.description)).ToList();
        var embeddings = embeddingService.GenerateEmbeddings(texts);

        var batchItems = new List<(string entityType, string entityKey, float[] embedding)>();
        for (int i = 0; i < items.Count; i++)
        {
            batchItems.Add(("CodeNode", items[i].node.Id.ToString(), embeddings[i]));
        }

        await vectorStore.StoreBatchEmbeddingsAsync(batchItems);

        logger.LogInformation("Indexed {Count} node embeddings", items.Count);
    }

    private static string BuildNodeText(GraphNode node, string? description)
    {
        var parts = new List<string>
        {
            node.Label.ToString(),
            node.Name,
            node.QualifiedName
        };

        if (!string.IsNullOrEmpty(description))
            parts.Add(description);

        if (!string.IsNullOrEmpty(node.Project))
            parts.Add(node.Project);

        return string.Join(" ", parts);
    }
}

public record SemanticSearchResult(GraphNode Node, double Score);

public interface ISemanticSearchService
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<SemanticSearchResult>> SearchAsync(string query, string? project = null, int topK = 10);
    Task IndexNodeAsync(GraphNode node, string? description = null);
    Task IndexNodeBatchAsync(IReadOnlyList<(GraphNode node, string? description)> items);
}
