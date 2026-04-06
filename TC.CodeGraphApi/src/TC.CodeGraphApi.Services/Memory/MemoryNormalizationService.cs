using System.Text.RegularExpressions;
using FuzzySharp;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models.Memory;
using TC.CodeGraphApi.Services.Embeddings;

namespace TC.CodeGraphApi.Services.Memory;

public partial class MemoryNormalizationService
{
    private readonly IMemoryGraphStore _store;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<MemoryNormalizationService> _logger;
    private const int FuzzyMatchThreshold = 90;
    private const int MaxEmbeddingConcurrency = 5;

    public MemoryNormalizationService(IMemoryGraphStore store, IEmbeddingService embedding, ILogger<MemoryNormalizationService> logger)
    {
        _store = store;
        _embedding = embedding;
        _logger = logger;
    }

    public async Task<StoreMemoryResult> NormalizeAndUpsertAsync(MemoryExtractionResult extraction, string source, string username)
    {
        var result = new StoreMemoryResult();
        var newIdsThisBatch = new List<string>();
        var idMapping = new Dictionary<string, string>();

        // Phase 1: Build entity list with embeddings
        var entities = new List<MemoryEntity>();
        var embeddings = new float[]?[extraction.Nodes.Count];

        if (_embedding.IsAvailable)
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, extraction.Nodes.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxEmbeddingConcurrency },
                (i, _) =>
                {
                    var node = extraction.Nodes[i];
                    embeddings[i] = GenerateEntityEmbedding(node.Label, node.Summary);
                    return ValueTask.CompletedTask;
                });
        }

        for (var i = 0; i < extraction.Nodes.Count; i++)
        {
            var node = extraction.Nodes[i];
            var normalizedId = ToSnakeCase(node.Id);

            var candidates = await _store.FindCandidateEntityIdsAsync(normalizedId, username);
            candidates.AddRange(newIdsThisBatch);
            var matchedId = FindFuzzyMatch(normalizedId, candidates);

            var actualId = matchedId ?? normalizedId;
            idMapping[node.Id] = actualId;

            if (matchedId != null)
                _logger.LogDebug("Fuzzy matched '{ExtractedId}' to existing '{MatchedId}'", normalizedId, matchedId);

            entities.Add(new MemoryEntity
            {
                Id = actualId,
                Label = node.Label,
                Type = node.Type,
                Summary = node.Summary,
                Source = node.Source ?? source,
                Username = username,
                Embedding = embeddings[i],
            });

            if (matchedId == null)
                newIdsThisBatch.Add(actualId);
        }

        await _store.UpsertEntitiesBatchAsync(entities);
        result.NodesWritten = entities.Count;

        // Phase 2: Build relationship list with embeddings
        var relationships = new List<MemoryRelationship>();
        var conflictEdges = new List<(string FromId, string ToId, MemoryExtractedEdge Edge)>();

        var edgeEmbeddings = new float[]?[extraction.Edges.Count];
        if (_embedding.IsAvailable)
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, extraction.Edges.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxEmbeddingConcurrency },
                (i, _) =>
                {
                    var edge = extraction.Edges[i];
                    try
                    {
                        var edgeText = $"{edge.Relationship}: {edge.Context}";
                        edgeEmbeddings[i] = _embedding.GenerateEmbedding(edgeText);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate embedding for edge '{Relationship}'", edge.Relationship);
                    }
                    return ValueTask.CompletedTask;
                });
        }

        for (var i = 0; i < extraction.Edges.Count; i++)
        {
            var edge = extraction.Edges[i];
            var fromId = idMapping.GetValueOrDefault(edge.From) ?? ToSnakeCase(edge.From);
            var toId = idMapping.GetValueOrDefault(edge.To) ?? ToSnakeCase(edge.To);

            relationships.Add(new MemoryRelationship
            {
                FromId = fromId,
                ToId = toId,
                RelationshipType = edge.Relationship,
                Context = edge.Context,
                Source = source,
                Timestamp = ParseTimestamp(edge.Timestamp),
                Embedding = edgeEmbeddings[i],
            });

            if (edge.Conflicts)
                conflictEdges.Add((fromId, toId, edge));
        }

        await _store.AddRelationshipsBatchAsync(relationships, username);
        result.EdgesWritten = relationships.Count;

        // Write conflict observations
        foreach (var (fromId, toId, edge) in conflictEdges)
        {
            var obs = new MemoryObservation
            {
                Id = $"obs_{Guid.NewGuid():N}",
                Claim = $"{fromId} {edge.Relationship} {toId}: {edge.Context}",
                ConflictsWith = $"Existing knowledge about {fromId} and {toId}",
                Source = source,
                Username = username,
            };
            await _store.CreateObservationAsync(obs, fromId, toId);
            result.ConflictsDetected++;
            _logger.LogInformation("Conflict detected: {Claim}", obs.Claim);
        }

        return result;
    }

    private float[]? GenerateEntityEmbedding(string label, string summary)
    {
        try
        {
            var text = $"{label}: {summary}";
            return _embedding.GenerateEmbedding(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate embedding for '{Label}'", label);
            return null;
        }
    }

    private static string? FindFuzzyMatch(string id, List<string> existingIds)
    {
        if (existingIds.Count == 0) return null;

        var bestMatch = Process.ExtractOne(id, existingIds);
        if (bestMatch != null && bestMatch.Score >= FuzzyMatchThreshold)
            return bestMatch.Value;

        return null;
    }

    internal static string ToSnakeCase(string input)
    {
        var cleaned = SpecialCharsRegex().Replace(input, " ");
        cleaned = CamelCaseRegex().Replace(cleaned, "$1 $2");
        cleaned = WhitespaceRegex().Replace(cleaned.Trim(), "_");
        return cleaned.ToLowerInvariant();
    }

    private static DateTime ParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp)) return DateTime.UtcNow;
        return DateTime.TryParse(timestamp, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_\s]")]
    private static partial Regex SpecialCharsRegex();

    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex CamelCaseRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
