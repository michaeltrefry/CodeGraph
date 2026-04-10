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

    public async Task<MemorySearchResult> SearchAsync(string query, int entityLimit = 5, int claimLimit = 5)
    {
        _logger.LogInformation("Searching memory v2 for query: {Query}", query);

        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return new MemorySearchResult();

        float[]? queryEmbedding = null;
        if (_embedding.IsAvailable)
        {
            try
            {
                var embedding = _embedding.GenerateEmbedding(normalizedQuery);
                if (embedding.Length > 0)
                    queryEmbedding = embedding;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate search embedding for memory query");
            }
        }

        var entitySeeds = await SearchEntitySeedsAsync(normalizedQuery, queryEmbedding, entityLimit);
        var claimSeeds = await _store.SearchClaimsAsync(normalizedQuery, queryEmbedding, claimLimit);

        return new MemorySearchResult
        {
            Query = normalizedQuery,
            Entities = entitySeeds,
            Claims = claimSeeds
                .OrderByDescending(seed => seed.Score)
                .Take(claimLimit)
                .Select(seed => new MemoryClaimSeed
                {
                    ClaimId = seed.Claim.Id,
                    NormalizedText = seed.Claim.NormalizedText,
                    Predicate = seed.Claim.Predicate,
                    Status = seed.Claim.Status,
                    Score = seed.Score,
                    MatchKind = seed.MatchKind,
                })
                .ToList(),
        };
    }

    public async Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphRequest request)
    {
        var normalizedQuery = request.Query?.Trim() ?? string.Empty;
        var clampedHops = Math.Clamp(request.MaxHops, 1, 5);
        var maxEntities = Math.Clamp(request.MaxReturnedEntities, 1, 100);
        var maxClaims = Math.Clamp(request.MaxReturnedClaims, 1, 200);

        List<MemoryEntitySeed> entitySeeds = [];
        List<MemoryClaimSeed> claimSeeds = [];

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var search = await SearchAsync(normalizedQuery, entityLimit: Math.Min(maxEntities, 10), claimLimit: Math.Min(maxClaims, 10));
            entitySeeds = search.Entities;
            claimSeeds = search.Claims;
        }

        var mergedSeedEntityIds = request.SeedEntityIds
            .Concat(entitySeeds.Select(seed => seed.EntityId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mergedSeedClaimIds = request.SeedClaimIds
            .Concat(claimSeeds.Select(seed => seed.ClaimId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var query = new MemorySubgraphQuery
        {
            Text = normalizedQuery,
            SeedEntityIds = mergedSeedEntityIds,
            SeedClaimIds = mergedSeedClaimIds,
        };

        var result = await _store.GetMemorySubgraphAsync(
            query,
            clampedHops,
            maxEntities,
            maxClaims,
            request.IncludeSuperseded,
            request.IncludeConflicts);

        result.Query = query;
        result.Seeds = new MemorySubgraphSeeds
        {
            Entities = entitySeeds.Where(seed => mergedSeedEntityIds.Contains(seed.EntityId, StringComparer.OrdinalIgnoreCase)).ToList(),
            Claims = claimSeeds.Where(seed => mergedSeedClaimIds.Contains(seed.ClaimId, StringComparer.OrdinalIgnoreCase)).ToList(),
        };

        if (result.Paths.Count == 0)
            result.Paths = BuildDirectSeedPaths(result);

        return result;
    }

    public async Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20)
    {
        return await _store.GetEntityBundleAsync(entityId, includeSuperseded, includeConflicts, neighborLimit);
    }

    public async Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true)
    {
        return await _store.GetClaimBundleAsync(claimId, includeSupersessionChain, includeConflicts, includeEvidence);
    }

    public async Task<MemoryFrontierExpansionResult> ExpandMemoryFrontierAsync(MemoryFrontierExpansionRequest request)
    {
        var frontierEntityIds = (request.FrontierEntityIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var frontierClaimIds = (request.FrontierClaimIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var additionalHops = Math.Clamp(request.MaxAdditionalHops, 1, 5);
        var frontierLimit = Math.Clamp(request.FrontierLimit, 1, 100);
        var minScore = Math.Clamp(request.MinScore, 0, 100);

        if (frontierEntityIds.Count == 0 && frontierClaimIds.Count == 0)
        {
            return new MemoryFrontierExpansionResult
            {
                Meta = new MemoryFrontierExpansionMeta
                {
                    AdditionalHopsUsed = 0,
                    FrontierExpanded = 0,
                    ResponseTruncated = false,
                },
            };
        }

        var subgraph = await GetMemorySubgraphAsync(new MemorySubgraphRequest
        {
            SeedEntityIds = frontierEntityIds,
            SeedClaimIds = frontierClaimIds,
            MaxHops = additionalHops,
            MaxReturnedEntities = Math.Clamp(frontierLimit * 2, 1, 100),
            MaxReturnedClaims = Math.Clamp(frontierLimit * 2, 1, 200),
            IncludeSuperseded = false,
            IncludeConflicts = true,
        });

        var candidateEntities = subgraph.Entities
            .Where(entity => !frontierEntityIds.Contains(entity.Entity.Id, StringComparer.OrdinalIgnoreCase))
            .Where(entity => entity.Score >= minScore)
            .ToDictionary(entity => entity.Entity.Id, StringComparer.OrdinalIgnoreCase);
        var candidateClaims = subgraph.Claims
            .Where(claim => !frontierClaimIds.Contains(claim.Claim.Id, StringComparer.OrdinalIgnoreCase))
            .Where(claim => claim.Score >= minScore)
            .ToDictionary(claim => claim.Claim.Id, StringComparer.OrdinalIgnoreCase);

        var rankedCandidates = candidateEntities.Values
            .Select(entity => new FrontierCandidate(entity.Entity.Id, "entity", entity.Score, entity.HopDistance, entity.Entity.Label))
            .Concat(candidateClaims.Values.Select(claim =>
                new FrontierCandidate(claim.Claim.Id, "claim", claim.Score, claim.HopDistance, claim.Claim.NormalizedText)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.HopDistance)
            .ThenBy(candidate => candidate.SortText, StringComparer.OrdinalIgnoreCase)
            .Take(frontierLimit)
            .ToList();

        var selectedEntityIds = rankedCandidates
            .Where(candidate => candidate.Kind == "entity")
            .Select(candidate => candidate.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedClaimIds = rankedCandidates
            .Where(candidate => candidate.Kind == "claim")
            .Select(candidate => candidate.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedEntities = rankedCandidates
            .Where(candidate => candidate.Kind == "entity")
            .Select(candidate => candidate.Id)
            .Select(id => candidateEntities[id])
            .ToList();
        var addedClaims = rankedCandidates
            .Where(candidate => candidate.Kind == "claim")
            .Select(candidate => candidate.Id)
            .Select(id => candidateClaims[id])
            .ToList();

        return new MemoryFrontierExpansionResult
        {
            AddedEntities = addedEntities,
            AddedClaims = addedClaims,
            Paths = BuildExpansionPaths(subgraph, frontierEntityIds, frontierClaimIds, selectedEntityIds, selectedClaimIds),
            Meta = new MemoryFrontierExpansionMeta
            {
                AdditionalHopsUsed = subgraph.Meta.MaxHopsUsed,
                FrontierExpanded = addedEntities.Count + addedClaims.Count,
                ResponseTruncated = subgraph.Meta.ResponseTruncated
                                    || candidateEntities.Count + candidateClaims.Count > rankedCandidates.Count,
            },
        };
    }

    public async Task<MemorySummaryRenderResult> RenderMemorySummaryAsync(MemorySummaryRenderRequest request)
    {
        var entityIds = (request.EntityIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var claimIds = (request.ClaimIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var style = NormalizeSummaryStyle(request.Style);

        if (entityIds.Count == 0 && claimIds.Count == 0)
            return new MemorySummaryRenderResult { Style = style, Text = "No relevant memories found." };

        var seedCount = Math.Max(entityIds.Count + claimIds.Count, 1);
        var subgraph = await GetMemorySubgraphAsync(new MemorySubgraphRequest
        {
            SeedEntityIds = entityIds,
            SeedClaimIds = claimIds,
            MaxHops = 1,
            MaxReturnedEntities = Math.Clamp(Math.Max(seedCount * 3, 10), 1, 100),
            MaxReturnedClaims = Math.Clamp(Math.Max(seedCount * 4, 20), 1, 200),
            IncludeSuperseded = true,
            IncludeConflicts = true,
        });

        return new MemorySummaryRenderResult
        {
            Style = style,
            Text = FormatSubgraphSummary(subgraph, style),
        };
    }

    public async Task<MemoryQueryResult> QueryAsync(string topic, int hops = 2, int maxNodes = 5, int maxRelsPerEntity = 8)
    {
        _logger.LogInformation("Querying memory for topic: {Topic}", topic);
        var subgraph = await GetMemorySubgraphAsync(new MemorySubgraphRequest
        {
            Query = topic,
            MaxHops = hops,
            MaxReturnedEntities = maxNodes,
            MaxReturnedClaims = Math.Max(maxNodes * 2, maxRelsPerEntity),
            IncludeSuperseded = false,
            IncludeConflicts = true,
        });

        if (subgraph.Entities.Count == 0 && subgraph.Claims.Count == 0)
        {
            _logger.LogInformation("No matching claim-centric memories found for topic: {Topic}", topic);
            return new MemoryQueryResult { FormattedText = "No relevant memories found." };
        }

        var entitiesWithRels = ConvertSubgraphToLegacyEntities(subgraph, maxRelsPerEntity);
        var conflicts = subgraph.Observations;

        _logger.LogInformation("Query returned {EntityCount} entities, {ConflictCount} conflicts",
            entitiesWithRels.Count, conflicts.Count);

        return new MemoryQueryResult
        {
            Entities = entitiesWithRels,
            Conflicts = conflicts,
            FormattedText = FormatSubgraphForLlm(subgraph, maxRelsPerEntity),
        };
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

    internal static string FormatSubgraphForLlm(MemorySubgraphResult subgraph, int maxClaimsPerEntity = 8)
    {
        return FormatSubgraphSummary(subgraph, "markdown", maxClaimsPerEntity);
    }

    internal static string FormatSubgraphSummary(MemorySubgraphResult subgraph, string style, int maxClaimsPerEntity = 8)
    {
        var normalizedStyle = NormalizeSummaryStyle(style);
        var sb = new StringBuilder();
        sb.AppendLine(normalizedStyle == "markdown" ? "## Relevant Memory" : "Relevant Memory");
        sb.AppendLine();

        var claimsBySubject = subgraph.Claims
            .GroupBy(item => item.Claim.SubjectEntityId)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Claim.RecordedAt)
                .Take(maxClaimsPerEntity)
                .ToList());

        foreach (var entity in subgraph.Entities
                     .OrderBy(item => item.HopDistance)
                     .ThenByDescending(item => item.Score))
        {
            var summary = string.IsNullOrWhiteSpace(entity.Entity.Summary)
                ? string.Empty
                : normalizedStyle == "markdown"
                    ? $" — {entity.Entity.Summary}"
                    : $" - {entity.Entity.Summary}";
            var entityLine = normalizedStyle == "markdown"
                ? $"**{entity.Entity.Label}** ({entity.Entity.Type}){summary}"
                : $"{entity.Entity.Label} ({entity.Entity.Type}){summary}";
            sb.AppendLine(entityLine);

            if (claimsBySubject.TryGetValue(entity.Entity.Id, out var claims))
            {
                foreach (var claim in claims)
                {
                    var prefix = claim.Claim.Status switch
                    {
                        MemoryClaimStatus.Conflicted => "  - conflicted: ",
                        MemoryClaimStatus.Superseded => "  - superseded: ",
                        MemoryClaimStatus.Deprecated => "  - deprecated: ",
                        _ => "  - ",
                    };
                    sb.AppendLine($"{prefix}{claim.Claim.NormalizedText}");
                }
            }

            var outgoingEdges = subgraph.EntityEdges
                .Where(edge => edge.FromEntityId.Equals(entity.Entity.Id, StringComparison.OrdinalIgnoreCase))
                .Take(maxClaimsPerEntity);
            foreach (var edge in outgoingEdges)
            {
                var target = subgraph.Entities.FirstOrDefault(candidate =>
                    candidate.Entity.Id.Equals(edge.ToEntityId, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                    sb.AppendLine($"  - {edge.EdgeType} -> {target.Entity.Label}");
            }

            sb.AppendLine();
        }

        var unanchoredClaims = subgraph.Claims
            .Where(claim => subgraph.Entities.All(entity =>
                !entity.Entity.Id.Equals(claim.Claim.SubjectEntityId, StringComparison.OrdinalIgnoreCase)))
            .Take(maxClaimsPerEntity)
            .ToList();
        if (unanchoredClaims.Count > 0)
        {
            sb.AppendLine(normalizedStyle == "markdown" ? "**Other Claims**" : "Other Claims");
            foreach (var claim in unanchoredClaims)
                sb.AppendLine($"  - {claim.Claim.NormalizedText}");
            sb.AppendLine();
        }

        foreach (var observation in subgraph.Observations)
        {
            sb.AppendLine($"⚠️ CONFLICT (unresolved): \"{observation.Claim}\" ({observation.Timestamp:yyyy-MM-dd})");
            sb.AppendLine($"   conflicts with: \"{observation.ConflictsWith}\"");
            sb.AppendLine();
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

    private async Task<List<MemoryEntitySeed>> SearchEntitySeedsAsync(string query, float[]? queryEmbedding, int limit)
    {
        var exactCandidates = new Dictionary<string, MemoryEntitySeed>(StringComparer.OrdinalIgnoreCase);
        var normalizedQuery = NormalizeSearchText(query);
        var queryTokens = TokenizeSearchText(query);

        var normalizedId = MemoryNormalizationService.ToSnakeCase(query);
        var exactEntity = await _store.GetEntityAsync(normalizedId) ?? await _store.GetEntityByExternalIdAsync(normalizedId);
        if (exactEntity != null)
        {
            exactCandidates[exactEntity.Id] = new MemoryEntitySeed
            {
                EntityId = exactEntity.Id,
                Label = exactEntity.Label,
                Type = exactEntity.Type,
                Score = 100,
                MatchKind = "exact",
            };
        }

        var textResults = await _store.TextSearchAsync(query, limit);
        foreach (var entity in textResults)
        {
            if (!PassesLexicalEntityFilter(entity, normalizedQuery, queryTokens))
                continue;

            var existing = exactCandidates.GetValueOrDefault(entity.Id);
            var score = existing?.Score ?? 0;
            exactCandidates[entity.Id] = new MemoryEntitySeed
            {
                EntityId = entity.Id,
                Label = entity.Label,
                Type = entity.Type,
                Score = Math.Max(score, 60 - textResults.IndexOf(entity)),
                MatchKind = existing?.MatchKind ?? "lexical",
            };
        }

        if (queryEmbedding != null)
        {
            var vectorResults = await _store.VectorSearchAsync(queryEmbedding, limit);
            foreach (var (entity, similarity) in vectorResults)
            {
                var existing = exactCandidates.GetValueOrDefault(entity.Id);
                var candidateScore = Math.Max(existing?.Score ?? 0, 40 + (similarity * 20));
                exactCandidates[entity.Id] = new MemoryEntitySeed
                {
                    EntityId = entity.Id,
                    Label = entity.Label,
                    Type = entity.Type,
                    Score = candidateScore,
                    MatchKind = existing?.MatchKind == "exact" ? "exact" : existing?.MatchKind ?? "vector",
                };
            }
        }

        var ranked = exactCandidates.Values
            .OrderByDescending(seed => seed.Score)
            .ThenBy(seed => seed.Label)
            .ToList();

        return PruneWeakEntitySeeds(ranked, limit);
    }

    internal static List<MemoryPathExplanation> BuildDirectSeedPaths(MemorySubgraphResult result)
    {
        var paths = new List<MemoryPathExplanation>();

        foreach (var entity in result.Entities.Where(entity => entity.IsDirectSeed))
        {
            paths.Add(new MemoryPathExplanation
            {
                SeedId = entity.Entity.Id,
                DestinationId = entity.Entity.Id,
                HopCount = 0,
                ScoreContribution = entity.Score,
                EdgeSequence = [],
            });
        }

        foreach (var claim in result.Claims.Where(claim => claim.IsDirectSeed))
        {
            paths.Add(new MemoryPathExplanation
            {
                SeedId = claim.Claim.Id,
                DestinationId = claim.Claim.Id,
                HopCount = 0,
                ScoreContribution = claim.Score,
                EdgeSequence = [],
            });
        }

        return paths;
    }

    internal static List<MemoryPathExplanation> BuildExpansionPaths(
        MemorySubgraphResult result,
        IReadOnlyList<string> frontierEntityIds,
        IReadOnlyList<string> frontierClaimIds,
        IReadOnlySet<string> destinationEntityIds,
        IReadOnlySet<string> destinationClaimIds)
    {
        var destinationKeys = destinationEntityIds
            .Select(ToEntityNodeKey)
            .Concat(destinationClaimIds.Select(ToClaimNodeKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (destinationKeys.Count == 0)
            return [];

        var scoreByNode = result.Entities.ToDictionary(
                entity => ToEntityNodeKey(entity.Entity.Id),
                entity => entity.Score,
                StringComparer.OrdinalIgnoreCase)
            .Concat(result.Claims.Select(claim =>
                new KeyValuePair<string, double>(ToClaimNodeKey(claim.Claim.Id), claim.Score)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var paths = result.Paths
            .Where(path => destinationKeys.Contains(ToGraphNodeKey(path.DestinationId, destinationEntityIds, destinationClaimIds)))
            .GroupBy(path => ToGraphNodeKey(path.DestinationId, destinationEntityIds, destinationClaimIds), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(path => path.HopCount).First())
            .ToDictionary(
                path => ToGraphNodeKey(path.DestinationId, destinationEntityIds, destinationClaimIds),
                path => path,
                StringComparer.OrdinalIgnoreCase);

        var adjacency = BuildTraversalAdjacency(result);
        var frontierKeys = frontierEntityIds
            .Select(ToEntityNodeKey)
            .Concat(frontierClaimIds.Select(ToClaimNodeKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previousByNode = new Dictionary<string, TraversalStep>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seedKey in frontierKeys)
        {
            visited.Add(seedKey);
            queue.Enqueue(seedKey);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in adjacency.GetValueOrDefault(current, []))
            {
                if (!visited.Add(edge.TargetKey))
                    continue;

                previousByNode[edge.TargetKey] = new TraversalStep(current, edge.EdgeType);
                queue.Enqueue(edge.TargetKey);
            }
        }

        foreach (var destinationKey in destinationKeys)
        {
            if (paths.ContainsKey(destinationKey) || !visited.Contains(destinationKey))
                continue;

            var edgeSequence = new List<string>();
            var cursor = destinationKey;
            while (previousByNode.TryGetValue(cursor, out var step))
            {
                edgeSequence.Add(step.EdgeType);
                cursor = step.PreviousKey;
            }

            edgeSequence.Reverse();
            paths[destinationKey] = new MemoryPathExplanation
            {
                SeedId = FromGraphNodeKey(cursor),
                DestinationId = FromGraphNodeKey(destinationKey),
                HopCount = edgeSequence.Count,
                ScoreContribution = scoreByNode.GetValueOrDefault(destinationKey),
                EdgeSequence = edgeSequence,
            };
        }

        return paths.Values
            .OrderBy(path => path.HopCount)
            .ThenByDescending(path => path.ScoreContribution)
            .ThenBy(path => path.DestinationId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();

        return string.Join(' ',
            new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    internal static List<string> TokenizeSearchText(string value) =>
        NormalizeSearchText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    internal static bool PassesLexicalEntityFilter(MemoryEntity entity, string normalizedQuery, IReadOnlyList<string> queryTokens)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        var haystacks = new List<string>
        {
            NormalizeSearchText(entity.Label),
            NormalizeSearchText(entity.Id),
            NormalizeSearchText(entity.ExternalId ?? string.Empty),
            NormalizeSearchText(entity.CanonicalName ?? string.Empty),
        };

        haystacks.AddRange(entity.Aliases.Select(NormalizeSearchText));
        haystacks = haystacks.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();

        if (haystacks.Any(haystack => haystack.Contains(normalizedQuery, StringComparison.Ordinal)))
            return true;

        if (queryTokens.Count <= 1)
            return haystacks.Any(haystack => haystack.Contains(normalizedQuery, StringComparison.Ordinal));

        return haystacks.Any(haystack => queryTokens.All(token => haystack.Contains(token, StringComparison.Ordinal)));
    }

    internal static List<MemoryEntitySeed> PruneWeakEntitySeeds(List<MemoryEntitySeed> rankedSeeds, int limit)
    {
        if (rankedSeeds.Count == 0)
            return [];

        var hasExact = rankedSeeds.Any(seed => seed.MatchKind == "exact");
        var minScore = hasExact ? 80 : 50;
        var pruned = rankedSeeds
            .Where(seed => seed.MatchKind == "exact" || seed.Score >= minScore)
            .Take(limit)
            .ToList();

        if (pruned.Count == 0)
            return rankedSeeds.Take(limit).ToList();

        return pruned;
    }

    private static List<MemoryEntityWithRelationships> ConvertSubgraphToLegacyEntities(
        MemorySubgraphResult subgraph,
        int maxRelsPerEntity)
    {
        var entitiesById = subgraph.Entities.ToDictionary(item => item.Entity.Id, item => item);
        var relationshipsBySource = new Dictionary<string, List<MemoryRelationshipDetail>>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in subgraph.EntityEdges)
        {
            if (entitiesById.TryGetValue(edge.ToEntityId, out var target))
            {
                relationshipsBySource.TryAdd(edge.FromEntityId, []);
                relationshipsBySource[edge.FromEntityId].Add(new MemoryRelationshipDetail
                {
                    Direction = "outgoing",
                    RelationshipType = edge.EdgeType,
                    TargetLabel = target.Entity.Label,
                    TargetId = target.Entity.Id,
                    Timestamp = edge.UpdatedAt,
                    SourceId = edge.FromEntityId,
                });
            }

            if (entitiesById.TryGetValue(edge.FromEntityId, out var source))
            {
                relationshipsBySource.TryAdd(edge.ToEntityId, []);
                relationshipsBySource[edge.ToEntityId].Add(new MemoryRelationshipDetail
                {
                    Direction = "incoming",
                    RelationshipType = edge.EdgeType,
                    TargetLabel = source.Entity.Label,
                    TargetId = source.Entity.Id,
                    Timestamp = edge.UpdatedAt,
                    SourceId = edge.ToEntityId,
                });
            }
        }

        return subgraph.Entities
            .OrderBy(item => item.HopDistance)
            .ThenByDescending(item => item.Score)
            .Select(item => new MemoryEntityWithRelationships
            {
                Entity = item.Entity,
                VectorScore = item.Score,
                Relationships = relationshipsBySource.GetValueOrDefault(item.Entity.Id, [])
                    .OrderByDescending(rel => rel.Timestamp)
                    .Take(maxRelsPerEntity)
                    .ToList(),
            })
            .ToList();
    }

    internal static string NormalizeSummaryStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
            return "markdown";

        return style.Trim().ToLowerInvariant() switch
        {
            "plain" => "plain",
            "text" => "plain",
            _ => "markdown",
        };
    }

    private static Dictionary<string, List<TraversalEdge>> BuildTraversalAdjacency(MemorySubgraphResult result)
    {
        var adjacency = new Dictionary<string, List<TraversalEdge>>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in result.EntityEdges)
        {
            AddTraversalEdge(adjacency, ToEntityNodeKey(edge.FromEntityId), ToEntityNodeKey(edge.ToEntityId), edge.EdgeType);
            AddTraversalEdge(adjacency, ToEntityNodeKey(edge.ToEntityId), ToEntityNodeKey(edge.FromEntityId), edge.EdgeType);
        }

        foreach (var edge in result.ClaimEdges)
        {
            AddTraversalEdge(adjacency, ToClaimNodeKey(edge.FromClaimId), ToClaimNodeKey(edge.ToClaimId), edge.EdgeType);
            AddTraversalEdge(adjacency, ToClaimNodeKey(edge.ToClaimId), ToClaimNodeKey(edge.FromClaimId), edge.EdgeType);
        }

        foreach (var claim in result.Claims)
        {
            AddTraversalEdge(adjacency, ToClaimNodeKey(claim.Claim.Id), ToEntityNodeKey(claim.Claim.SubjectEntityId), "SUBJECT");
            AddTraversalEdge(adjacency, ToEntityNodeKey(claim.Claim.SubjectEntityId), ToClaimNodeKey(claim.Claim.Id), "SUBJECT");

            if (!string.IsNullOrWhiteSpace(claim.Claim.ObjectEntityId))
            {
                AddTraversalEdge(adjacency, ToClaimNodeKey(claim.Claim.Id), ToEntityNodeKey(claim.Claim.ObjectEntityId!), "OBJECT");
                AddTraversalEdge(adjacency, ToEntityNodeKey(claim.Claim.ObjectEntityId!), ToClaimNodeKey(claim.Claim.Id), "OBJECT");
            }
        }

        return adjacency;
    }

    private static void AddTraversalEdge(
        Dictionary<string, List<TraversalEdge>> adjacency,
        string fromKey,
        string toKey,
        string edgeType)
    {
        adjacency.TryAdd(fromKey, []);
        adjacency[fromKey].Add(new TraversalEdge(toKey, edgeType));
    }

    private static string ToEntityNodeKey(string entityId) => $"entity:{entityId}";

    private static string ToClaimNodeKey(string claimId) => $"claim:{claimId}";

    private static string ToGraphNodeKey(
        string rawId,
        IReadOnlySet<string> destinationEntityIds,
        IReadOnlySet<string> destinationClaimIds)
    {
        if (destinationEntityIds.Contains(rawId))
            return ToEntityNodeKey(rawId);

        if (destinationClaimIds.Contains(rawId))
            return ToClaimNodeKey(rawId);

        return rawId;
    }

    private static string FromGraphNodeKey(string graphNodeKey)
    {
        var separatorIndex = graphNodeKey.IndexOf(':');
        return separatorIndex >= 0 ? graphNodeKey[(separatorIndex + 1)..] : graphNodeKey;
    }

    private sealed record FrontierCandidate(string Id, string Kind, double Score, int HopDistance, string SortText);

    private sealed record TraversalEdge(string TargetKey, string EdgeType);

    private sealed record TraversalStep(string PreviousKey, string EdgeType);
}
