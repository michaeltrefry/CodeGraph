using System.Text.Json;
using CodeGraph.Data;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public class MySqlVectorStore(IOptions<MariaDbStorageOptions> optionsAccessor) : IVectorStore
{
    private readonly MariaDbStorageOptions options = optionsAccessor.Value;

    static MySqlVectorStore()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Task StoreEmbeddingAsync(string entityType, string entityKey, float[] embedding)
        => StoreBatchEmbeddingsAsync([(entityType, entityKey, embedding)]);

    public async Task StoreBatchEmbeddingsAsync(IReadOnlyList<(string entityType, string entityKey, float[] embedding)> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using var conn = await GetOpenConnectionAsync();

        foreach (var batch in items.Chunk(options.BatchSize))
        {
            await conn.ExecuteAsync("""
                INSERT INTO embeddings (entity_type, entity_key, embedding_json)
                VALUES (@EntityType, @EntityKey, @EmbeddingJson)
                ON DUPLICATE KEY UPDATE
                    embedding_json = VALUES(embedding_json),
                    updated_at = CURRENT_TIMESTAMP(3)
                """, batch.Select(item => new
            {
                EntityType = item.entityType,
                EntityKey = item.entityKey,
                EmbeddingJson = JsonSerializer.Serialize(item.embedding)
            }));
        }
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchSimilarAsync(
        float[] queryEmbedding,
        string? entityType = null,
        int topK = 10,
        double minScore = 0.5)
    {
        await using var conn = await GetOpenConnectionAsync();

        var rows = await conn.QueryAsync<EmbeddingRow>(
            entityType is null
                ? "SELECT entity_type, entity_key, embedding_json FROM embeddings"
                : "SELECT entity_type, entity_key, embedding_json FROM embeddings WHERE entity_type = @EntityType",
            new { EntityType = entityType });

        return rows
            .Select(row => new VectorSearchResult(
                row.EntityType,
                row.EntityKey,
                CosineSimilarity(queryEmbedding, DeserializeEmbedding(row.EmbeddingJson))))
            .Where(result => result.Score >= minScore)
            .OrderByDescending(result => result.Score)
            .Take(topK)
            .ToList();
    }

    public async Task DeleteEmbeddingsAsync(string entityType, string entityKey)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync("""
            DELETE FROM embeddings
            WHERE entity_type = @EntityType AND entity_key = @EntityKey
            """, new { EntityType = entityType, EntityKey = entityKey });
    }

    private async Task<MySqlConnection> GetOpenConnectionAsync()
    {
        var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static float[] DeserializeEmbedding(string json)
        => JsonSerializer.Deserialize<float[]>(json) ?? [];

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return 0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude == 0 || rightMagnitude == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private sealed record EmbeddingRow(string EntityType, string EntityKey, string EmbeddingJson);
}
