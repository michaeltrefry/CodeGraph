using System.Text;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Embeddings;

namespace CodeGraph.Services.Memory;

public class MemoryRetrievalService
{
    private readonly IMemoryGraphStore _store;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<MemoryRetrievalService> _logger;

    public MemoryRetrievalService(IMemoryGraphStore store, IEmbeddingService embedding, ILogger<MemoryRetrievalService> logger)
    {
        _store = store;
        _embedding = embedding;
        _logger = logger;
    }

    public async Task<MemoryQueryResult> QueryAsync(string topic, int hops = 2, int maxNodes = 5, int maxRelsPerEntity = 8)
    {
        _logger.LogInformation("Querying memory for topic: {Topic}", topic);

        var (seedEntities, queryEmbedding) = await FindSeedEntitiesAsync(topic, maxNodes);

        if (seedEntities.Count == 0)
        {
            _logger.LogInformation("No matching entities found for topic: {Topic}", topic);
            return new MemoryQueryResult { FormattedText = "No relevant memories found." };
        }

        var seedIds = seedEntities.Select(s => s.Entity.Id).ToList();
        var seedScores = seedEntities.ToDictionary(s => s.Entity.Id, s => s.Score);

        var (entities, relationships) = await _store.GetSubgraphAsync(seedIds, hops, maxNodes);

        var relsByEntity = relationships.GroupBy(r => r.SourceId)
            .ToDictionary(g => g.Key!, g => g.ToList());

        var entitiesWithRels = entities.Select(e => new MemoryEntityWithRelationships
        {
            Entity = e,
            VectorScore = seedScores.GetValueOrDefault(e.Id, 0.0),
            Relationships = relsByEntity.GetValueOrDefault(e.Id, []),
        })
        .OrderByDescending(e => e.VectorScore)
        .ThenByDescending(e => e.Entity.UpdatedAt)
        .ToList();

        var allEntityIds = entities.Select(e => e.Id).Distinct().ToList();
        var conflicts = await _store.GetUnresolvedObservationsForEntitiesAsync(allEntityIds);

        var result = new MemoryQueryResult
        {
            Entities = entitiesWithRels,
            Conflicts = conflicts,
            FormattedText = FormatForLlm(entitiesWithRels, conflicts, queryEmbedding, maxRelsPerEntity),
        };

        _logger.LogInformation("Query returned {EntityCount} entities, {ConflictCount} conflicts",
            entitiesWithRels.Count, conflicts.Count);

        return result;
    }

    private async Task<(List<(MemoryEntity Entity, double Score)> Entities, float[]? Embedding)> FindSeedEntitiesAsync(
        string topic, int maxNodes)
    {
        if (_embedding.IsAvailable)
        {
            try
            {
                var embedding = _embedding.GenerateEmbedding(topic);
                if (embedding.Length > 0)
                {
                    var vectorResults = await _store.VectorSearchAsync(embedding, maxNodes);
                    if (vectorResults.Count > 0)
                        return (vectorResults, embedding);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector search unavailable, falling back to text search");
            }
        }

        var textResults = await _store.TextSearchAsync(topic, maxNodes);
        return (textResults.Select(e => (e, 1.0)).ToList(), null);
    }

    internal static string FormatForLlm(List<MemoryEntityWithRelationships> entities, List<MemoryObservation> conflicts,
        float[]? queryEmbedding, int maxRelsPerEntity = 8)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Relevant Memory");
        sb.AppendLine();

        foreach (var ewr in entities)
        {
            var e = ewr.Entity;
            sb.AppendLine($"**{e.Label}** ({e.Type}) — {e.Summary}");

            var deduped = ewr.Relationships
                .GroupBy(r => (r.Direction, r.RelationshipType, r.TargetId))
                .Select(g => g.OrderByDescending(r => r.Timestamp).First());

            IEnumerable<MemoryRelationshipDetail> ranked;
            if (queryEmbedding != null)
            {
                ranked = deduped
                    .OrderByDescending(r => r.Embedding != null
                        ? CosineSimilarity(queryEmbedding, r.Embedding)
                        : 0.0)
                    .ThenByDescending(r => r.Timestamp);
            }
            else
            {
                ranked = deduped.OrderByDescending(r => r.Timestamp);
            }

            var topRels = ranked.Take(maxRelsPerEntity).ToList();

            foreach (var rel in topRels)
            {
                var arrow = rel.Direction == "outgoing" ? "→" : "←";
                var line = $"  - {rel.RelationshipType} {arrow} {rel.TargetLabel}";
                if (!string.IsNullOrEmpty(rel.Context))
                    line += $": {rel.Context}";
                sb.AppendLine(line);
            }

            sb.AppendLine();
        }

        if (conflicts.Count > 0)
        {
            foreach (var obs in conflicts)
            {
                sb.AppendLine($"⚠️ CONFLICT (unresolved): \"{obs.Claim}\" ({obs.Timestamp:yyyy-MM-dd})");
                sb.AppendLine($"   conflicts with: \"{obs.ConflictsWith}\"");
                sb.AppendLine("   Both memories are retained. This should be discussed to determine current accuracy.");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0.0;

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = Math.Sqrt(magA) * Math.Sqrt(magB);
        return magnitude == 0 ? 0.0 : dot / magnitude;
    }
}
