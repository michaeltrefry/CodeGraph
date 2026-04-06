namespace CodeGraph.Data;

public interface IVectorStore
{
    Task StoreEmbeddingAsync(string entityType, string entityKey, float[] embedding);
    Task StoreBatchEmbeddingsAsync(IReadOnlyList<(string entityType, string entityKey, float[] embedding)> items);
    Task<IReadOnlyList<VectorSearchResult>> SearchSimilarAsync(
        float[] queryEmbedding, string? entityType = null, int topK = 10, double minScore = 0.5);
    Task DeleteEmbeddingsAsync(string entityType, string entityKey);
}

public record VectorSearchResult(string EntityType, string EntityKey, double Score);
