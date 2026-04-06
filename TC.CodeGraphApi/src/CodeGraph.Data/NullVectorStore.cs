namespace CodeGraph.Data;

/// <summary>
/// No-op vector store for MySQL deployments where vector search is not available.
/// </summary>
public class NullVectorStore : IVectorStore
{
    public Task StoreEmbeddingAsync(string entityType, string entityKey, float[] embedding)
        => Task.CompletedTask;

    public Task StoreBatchEmbeddingsAsync(IReadOnlyList<(string entityType, string entityKey, float[] embedding)> items)
        => Task.CompletedTask;

    public Task<IReadOnlyList<VectorSearchResult>> SearchSimilarAsync(
        float[] queryEmbedding, string? entityType = null, int topK = 10, double minScore = 0.5)
        => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);

    public Task DeleteEmbeddingsAsync(string entityType, string entityKey)
        => Task.CompletedTask;
}
