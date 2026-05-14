using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

/// <summary>
/// Vector store implementation using Neo4j's native vector indexes.
/// Stores embeddings as properties on existing nodes or dedicated Embedding nodes.
/// </summary>
public class Neo4jVectorStore(Neo4jSessionFactory sessionFactory) : IVectorStore
{
    public async Task StoreEmbeddingAsync(string entityType, string entityKey, float[] embedding)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (e:Embedding {entityType: $entityType, entityKey: $entityKey})
                SET e.vector = $vector
                """,
                new
                {
                    entityType,
                    entityKey,
                    vector = embedding.Select(f => (double)f).ToList()
                });
        });
    }

    public async Task StoreBatchEmbeddingsAsync(
        IReadOnlyList<(string entityType, string entityKey, float[] embedding)> items)
    {
        if (items.Count == 0) return;

        await using var session = sessionFactory.GetSession();

        // Process in batches of 500
        for (int i = 0; i < items.Count; i += 500)
        {
            var batch = items.Skip(i).Take(500).Select(item => new Dictionary<string, object?>
            {
                ["entityType"] = item.entityType,
                ["entityKey"] = item.entityKey,
                ["vector"] = item.embedding.Select(f => (double)f).ToList()
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $items AS item
                    MERGE (e:Embedding {entityType: item.entityType, entityKey: item.entityKey})
                    SET e.vector = item.vector
                    """,
                    new { items = batch });
            });
        }
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchSimilarAsync(
        float[] queryEmbedding, string? entityType = null, int topK = 10, double minScore = 0.5)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            // Use Neo4j vector index for similarity search
            var vectorParam = queryEmbedding.Select(f => (double)f).ToList();

            var cypher = entityType is null
                ? """
                    CALL db.index.vector.queryNodes('embedding_vector', $topK, $vector)
                    YIELD node, score
                    WHERE score >= $minScore
                    RETURN node.entityType AS entityType, node.entityKey AS entityKey, score
                    ORDER BY score DESC
                    """
                : """
                    CALL db.index.vector.queryNodes('embedding_vector', $topK, $vector)
                    YIELD node, score
                    WHERE score >= $minScore AND node.entityType = $entityType
                    RETURN node.entityType AS entityType, node.entityKey AS entityKey, score
                    ORDER BY score DESC
                    """;

            try
            {
                var cursor = await tx.RunAsync(cypher, new { topK, vector = vectorParam, minScore, entityType });
                var results = new List<VectorSearchResult>();
                await foreach (var record in cursor)
                {
                    results.Add(new VectorSearchResult(
                        record["entityType"].As<string>(),
                        record["entityKey"].As<string>(),
                        record["score"].As<double>()));
                }
                return results;
            }
            catch (ClientException ex) when (ex.Message.Contains("index") || ex.Message.Contains("vector"))
            {
                // Vector index may not exist yet — return empty results
                return new List<VectorSearchResult>();
            }
        });
    }

    public async Task DeleteEmbeddingsAsync(string entityType, string entityKey)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (e:Embedding {entityType: $entityType, entityKey: $entityKey}) DELETE e",
                new { entityType, entityKey });
        });
    }

    public async Task DeleteEmbeddingsByKeyPrefixAsync(string entityType, string entityKeyPrefix)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (e:Embedding {entityType: $entityType}) WHERE e.entityKey STARTS WITH $entityKeyPrefix DELETE e",
                new { entityType, entityKeyPrefix });
        });
    }
}
