using System.Security.Cryptography;
using System.Text;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Embeddings;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Memory;

public class MemoryClaimIngestionService(
    IMemoryGraphStore store,
    IEmbeddingService embedding,
    ILogger<MemoryClaimIngestionService> logger)
{
    private const int MaxEmbeddingConcurrency = 5;

    public async Task<StoreMemoryResult> NormalizeAndUpsertClaimsAsync(
        MemoryClaimExtractionResult extraction,
        string source)
    {
        var result = new StoreMemoryResult();
        var entityMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entitiesById = new Dictionary<string, MemoryEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var extractedEntity in extraction.Entities)
        {
            var resolved = await ResolveOrCreateEntityAsync(extractedEntity, source, entitiesById);
            entityMap[extractedEntity.Id] = resolved.Id;
            entitiesById[resolved.Id] = resolved;
        }

        foreach (var claim in extraction.Claims)
        {
            var subjectId = await EnsureEntityReferenceAsync(claim.Subject, source, entityMap, entitiesById);
            if (!string.IsNullOrWhiteSpace(claim.Object))
                await EnsureEntityReferenceAsync(claim.Object, source, entityMap, entitiesById);
        }

        await PopulateEntityEmbeddingsAsync(entitiesById.Values.ToList());
        await store.UpsertEntitiesBatchAsync(entitiesById.Values.ToList());
        result.NodesWritten = entitiesById.Count;

        var claimsToUpsert = new Dictionary<string, MemoryClaim>(StringComparer.OrdinalIgnoreCase);
        var claimEdges = new List<MemoryClaimEdge>();
        var entityEdges = new Dictionary<(string FromId, string ToId, string EdgeType), MemoryEntityEdge>();
        var observations = new List<MemoryObservation>();
        var claimIdByInputRef = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var extractedClaim in extraction.Claims)
        {
            var subjectId = entityMap.GetValueOrDefault(extractedClaim.Subject)
                ?? MemoryNormalizationService.ToSnakeCase(extractedClaim.Subject);
            var objectId = !string.IsNullOrWhiteSpace(extractedClaim.Object)
                ? entityMap.GetValueOrDefault(extractedClaim.Object!)
                    ?? MemoryNormalizationService.ToSnakeCase(extractedClaim.Object!)
                : null;
            var predicate = NormalizePredicate(extractedClaim.Predicate);
            var normalizedValueText = NormalizeOptionalText(extractedClaim.ValueText);
            var normalizedValueJson = NormalizeOptionalText(extractedClaim.ValueJson);
            var normalizedText = string.IsNullOrWhiteSpace(extractedClaim.NormalizedText)
                ? BuildNormalizedText(subjectId, predicate, objectId, normalizedValueText, normalizedValueJson)
                : NormalizeFreeText(extractedClaim.NormalizedText);

            var claimKey = ComputeClaimKey(subjectId, predicate, objectId, normalizedValueText, normalizedValueJson, normalizedText);
            var factGroupKey = ComputeFactGroupKey(subjectId, predicate, objectId, normalizedValueText, normalizedValueJson);
            var claimId = string.IsNullOrWhiteSpace(extractedClaim.Id)
                ? $"claim_{claimKey[..16]}"
                : MemoryNormalizationService.ToSnakeCase(extractedClaim.Id);

            claimIdByInputRef[claimId] = claimId;
            if (!string.IsNullOrWhiteSpace(extractedClaim.Id))
                claimIdByInputRef[extractedClaim.Id] = claimId;

            var existingFactGroupClaims = await store.GetClaimsByFactGroupAsync(factGroupKey);
            var duplicate = existingFactGroupClaims.FirstOrDefault(c =>
                c.ClaimKey.Equals(claimKey, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                claimIdByInputRef[claimId] = duplicate.Id;
                if (!string.IsNullOrWhiteSpace(extractedClaim.Id))
                    claimIdByInputRef[extractedClaim.Id] = duplicate.Id;
                continue;
            }

            var claim = new MemoryClaim
            {
                Id = claimId,
                ClaimKey = claimKey,
                FactGroupKey = factGroupKey,
                SubjectEntityId = subjectId,
                Predicate = predicate,
                ObjectEntityId = objectId,
                ValueText = extractedClaim.ValueText,
                ValueJson = extractedClaim.ValueJson,
                NormalizedText = normalizedText,
                Status = MemoryClaimStatus.Active,
                Confidence = extractedClaim.Confidence,
                EffectiveAt = ParseTimestamp(extractedClaim.EffectiveAt),
                RecordedAt = ParseTimestamp(extractedClaim.RecordedAt) ?? DateTime.UtcNow,
                SupersedesClaimId = null,
                Source = extractedClaim.Source ?? source,
            };

            var updates = new List<MemoryClaim>();
            var explicitSupersedesId = ResolveSupersededClaimId(extractedClaim.Supersedes, claimIdByInputRef);
            if (!string.IsNullOrWhiteSpace(explicitSupersedesId))
            {
                claim.SupersedesClaimId = explicitSupersedesId;
                claimEdges.Add(new MemoryClaimEdge
                {
                    FromClaimId = claim.Id,
                    ToClaimId = explicitSupersedesId,
                    EdgeType = "supersedes",
                    Source = claim.Source,
                });
            }

            var activeEquivalentClaims = existingFactGroupClaims
                .Where(c => c.Status == MemoryClaimStatus.Active)
                .ToList();

            if (activeEquivalentClaims.Count > 0)
            {
                var newestEquivalent = activeEquivalentClaims
                    .OrderByDescending(GetClaimSortTime)
                    .First();

                if (GetClaimSortTime(claim) >= GetClaimSortTime(newestEquivalent))
                {
                    foreach (var equivalent in activeEquivalentClaims)
                    {
                        equivalent.Status = MemoryClaimStatus.Superseded;
                        updates.Add(equivalent);
                        claimEdges.Add(new MemoryClaimEdge
                        {
                            FromClaimId = claim.Id,
                            ToClaimId = equivalent.Id,
                            EdgeType = "supersedes",
                            Source = claim.Source,
                        });
                    }
                }
                else
                {
                    claim.Status = MemoryClaimStatus.Superseded;
                    claim.SupersedesClaimId = newestEquivalent.Id;
                    claimEdges.Add(new MemoryClaimEdge
                    {
                        FromClaimId = newestEquivalent.Id,
                        ToClaimId = claim.Id,
                        EdgeType = "supersedes",
                        Source = claim.Source,
                    });
                }
            }

            var existingSubjectPredicateClaims = await store.GetClaimsBySubjectPredicateAsync(subjectId, predicate);
            var conflictingClaims = existingSubjectPredicateClaims
                .Where(c => c.Status == MemoryClaimStatus.Active && !c.FactGroupKey.Equals(factGroupKey, StringComparison.OrdinalIgnoreCase))
                .Where(c => !IsSameResolvedValue(c, objectId, normalizedValueText, normalizedValueJson))
                .ToList();

            if (conflictingClaims.Count > 0)
            {
                if (claim.Status == MemoryClaimStatus.Active)
                    claim.Status = MemoryClaimStatus.Conflicted;

                foreach (var conflictingClaim in conflictingClaims)
                {
                    claimEdges.Add(new MemoryClaimEdge
                    {
                        FromClaimId = claim.Id,
                        ToClaimId = conflictingClaim.Id,
                        EdgeType = "conflicts_with",
                        Source = claim.Source,
                    });

                    var aboutEntityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        claim.SubjectEntityId,
                    };
                    if (!string.IsNullOrWhiteSpace(claim.ObjectEntityId))
                        aboutEntityIds.Add(claim.ObjectEntityId);
                    if (!string.IsNullOrWhiteSpace(conflictingClaim.SubjectEntityId))
                        aboutEntityIds.Add(conflictingClaim.SubjectEntityId);
                    if (!string.IsNullOrWhiteSpace(conflictingClaim.ObjectEntityId))
                        aboutEntityIds.Add(conflictingClaim.ObjectEntityId);

                    observations.Add(new MemoryObservation
                    {
                        Id = $"obs_{Guid.NewGuid():N}",
                        Claim = claim.NormalizedText,
                        ConflictsWith = conflictingClaim.NormalizedText,
                        Source = claim.Source,
                        AboutEntityIds = aboutEntityIds.ToList(),
                        AboutClaimIds = [claim.Id, conflictingClaim.Id],
                    });
                }
            }

            foreach (var update in updates)
                claimsToUpsert[update.Id] = update;

            claimsToUpsert[claim.Id] = claim;
            claimIdByInputRef[claim.Id] = claim.Id;
            if (!string.IsNullOrWhiteSpace(extractedClaim.Id))
                claimIdByInputRef[extractedClaim.Id] = claim.Id;

            if (claim.Status == MemoryClaimStatus.Active && !string.IsNullOrWhiteSpace(claim.ObjectEntityId))
            {
                var entityEdge = new MemoryEntityEdge
                {
                    FromEntityId = claim.SubjectEntityId,
                    ToEntityId = claim.ObjectEntityId,
                    EdgeType = claim.Predicate,
                    BestActiveClaimId = claim.Id,
                };
                entityEdges[(entityEdge.FromEntityId, entityEdge.ToEntityId, entityEdge.EdgeType)] = entityEdge;
            }
        }

        await PopulateClaimEmbeddingsAsync(claimsToUpsert.Values.ToList());
        await store.UpsertClaimsBatchAsync(claimsToUpsert.Values.ToList());
        await store.AddClaimEdgesBatchAsync(claimEdges);
        await store.UpsertEntityEdgesBatchAsync(entityEdges.Values.ToList());

        foreach (var observation in observations)
            await store.CreateObservationAsync(observation);

        var evidenceToWrite = new List<MemoryEvidence>();
        foreach (var extractedEvidence in extraction.Evidence)
        {
            if (string.IsNullOrWhiteSpace(extractedEvidence.ClaimId) && string.IsNullOrWhiteSpace(extractedEvidence.ObservationId))
                continue;

            var claimId = !string.IsNullOrWhiteSpace(extractedEvidence.ClaimId)
                ? claimIdByInputRef.GetValueOrDefault(extractedEvidence.ClaimId!, extractedEvidence.ClaimId)
                : null;

            evidenceToWrite.Add(new MemoryEvidence
            {
                Id = $"evidence_{Guid.NewGuid():N}",
                ClaimId = claimId,
                ObservationId = extractedEvidence.ObservationId,
                EvidenceType = extractedEvidence.EvidenceType,
                SourceRef = extractedEvidence.SourceRef,
                Snippet = extractedEvidence.Snippet,
                MetadataJson = extractedEvidence.MetadataJson,
            });
        }

        await store.AddEvidenceBatchAsync(evidenceToWrite);

        result.ClaimsWritten = claimsToUpsert.Count;
        result.EdgesWritten = claimEdges.Count + entityEdges.Count;
        result.ConflictsDetected = observations.Count;
        result.ObservationsWritten = observations.Count;
        result.EvidenceWritten = evidenceToWrite.Count;

        logger.LogInformation(
            "Stored claim-centric memory batch: {EntityCount} entities, {ClaimCount} claims, {ClaimEdgeCount} claim edges, {EvidenceCount} evidence rows",
            entitiesById.Count, claimsToUpsert.Count, claimEdges.Count, evidenceToWrite.Count);

        return result;
    }

    internal static string NormalizePredicate(string predicate) =>
        MemoryNormalizationService.ToSnakeCase(predicate);

    internal static string NormalizeFreeText(string text) =>
        string.Join(' ', text
            .Trim()
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    internal static string ComputeClaimKey(
        string subjectEntityId,
        string predicate,
        string? objectEntityId,
        string? normalizedValueText,
        string? normalizedValueJson,
        string normalizedText)
    {
        return ComputeHash($"{subjectEntityId}|{predicate}|{objectEntityId}|{normalizedValueText}|{normalizedValueJson}|{normalizedText}");
    }

    internal static string ComputeFactGroupKey(
        string subjectEntityId,
        string predicate,
        string? objectEntityId,
        string? normalizedValueText,
        string? normalizedValueJson)
    {
        return ComputeHash($"{subjectEntityId}|{predicate}|{objectEntityId}|{normalizedValueText}|{normalizedValueJson}");
    }

    private async Task<MemoryEntity> ResolveOrCreateEntityAsync(
        MemoryExtractedEntity extractedEntity,
        string source,
        IReadOnlyDictionary<string, MemoryEntity> pendingEntities)
    {
        var normalizedId = MemoryNormalizationService.ToSnakeCase(extractedEntity.Id);
        var normalizedExternalId = NormalizeOptionalText(extractedEntity.ExternalId) ?? normalizedId;

        if (pendingEntities.TryGetValue(normalizedExternalId, out var pendingEntity))
            return pendingEntity;

        var existingByExternalId = await store.GetEntityByExternalIdAsync(normalizedExternalId);
        if (existingByExternalId != null)
            return MergeEntityMetadata(existingByExternalId, extractedEntity, source, normalizedExternalId);

        var existingById = await store.GetEntityAsync(normalizedExternalId) ?? await store.GetEntityAsync(normalizedId);
        if (existingById != null)
            return MergeEntityMetadata(existingById, extractedEntity, source, normalizedExternalId);

        return new MemoryEntity
        {
            Id = normalizedExternalId,
            ExternalId = normalizedExternalId,
            CanonicalName = extractedEntity.CanonicalName?.Trim(),
            Aliases = NormalizeAliases(extractedEntity.Aliases),
            Label = extractedEntity.Label,
            Type = extractedEntity.Type,
            Summary = extractedEntity.Summary ?? string.Empty,
            Source = extractedEntity.Source ?? source,
        };
    }

    private async Task<string> EnsureEntityReferenceAsync(
        string reference,
        string source,
        IDictionary<string, string> entityMap,
        IDictionary<string, MemoryEntity> entitiesById)
    {
        if (entityMap.TryGetValue(reference, out var mapped))
            return mapped;

        var normalized = MemoryNormalizationService.ToSnakeCase(reference);
        var existing = await store.GetEntityByExternalIdAsync(normalized) ?? await store.GetEntityAsync(normalized);
        var resolvedId = existing?.Id ?? normalized;

        if (!entitiesById.ContainsKey(resolvedId))
        {
            entitiesById[resolvedId] = existing ?? new MemoryEntity
            {
                Id = resolvedId,
                ExternalId = normalized,
                Label = HumanizeId(normalized),
                Type = "concept",
                Summary = string.Empty,
                Source = source,
            };
        }

        entityMap[reference] = resolvedId;
        return resolvedId;
    }

    private async Task PopulateEntityEmbeddingsAsync(List<MemoryEntity> entities)
    {
        if (!embedding.IsAvailable || entities.Count == 0)
            return;

        await Parallel.ForEachAsync(
            entities,
            new ParallelOptions { MaxDegreeOfParallelism = MaxEmbeddingConcurrency },
            (entity, _) =>
            {
                try
                {
                    entity.Embedding = embedding.GenerateEmbedding($"{entity.Label}: {entity.Summary}");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to generate memory entity embedding for {EntityId}", entity.Id);
                }

                return ValueTask.CompletedTask;
            });
    }

    private async Task PopulateClaimEmbeddingsAsync(List<MemoryClaim> claims)
    {
        if (!embedding.IsAvailable || claims.Count == 0)
            return;

        await Parallel.ForEachAsync(
            claims,
            new ParallelOptions { MaxDegreeOfParallelism = MaxEmbeddingConcurrency },
            (claim, _) =>
            {
                try
                {
                    claim.Embedding = embedding.GenerateEmbedding(claim.NormalizedText);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to generate memory claim embedding for {ClaimId}", claim.Id);
                }

                return ValueTask.CompletedTask;
            });
    }

    private static MemoryEntity MergeEntityMetadata(
        MemoryEntity existing,
        MemoryExtractedEntity extracted,
        string source,
        string normalizedExternalId)
    {
        existing.ExternalId ??= normalizedExternalId;
        existing.CanonicalName ??= extracted.CanonicalName?.Trim();
        if (existing.Aliases.Count == 0 && extracted.Aliases.Count > 0)
            existing.Aliases = NormalizeAliases(extracted.Aliases);
        if (string.IsNullOrWhiteSpace(existing.Summary) && !string.IsNullOrWhiteSpace(extracted.Summary))
            existing.Summary = extracted.Summary;
        existing.Source = extracted.Source ?? existing.Source ?? source;
        existing.UpdatedAt = DateTime.UtcNow;
        return existing;
    }

    private static List<string> NormalizeAliases(IEnumerable<string> aliases) =>
        aliases
            .Select(alias => alias.Trim())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return NormalizeFreeText(value);
    }

    private static string BuildNormalizedText(
        string subjectEntityId,
        string predicate,
        string? objectEntityId,
        string? normalizedValueText,
        string? normalizedValueJson)
    {
        var sb = new StringBuilder();
        sb.Append(subjectEntityId);
        sb.Append(' ');
        sb.Append(predicate);

        if (!string.IsNullOrWhiteSpace(objectEntityId))
        {
            sb.Append(' ');
            sb.Append(objectEntityId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedValueText))
        {
            sb.Append(' ');
            sb.Append(normalizedValueText);
        }

        if (!string.IsNullOrWhiteSpace(normalizedValueJson))
        {
            sb.Append(' ');
            sb.Append(normalizedValueJson);
        }

        return sb.ToString().Trim();
    }

    private static DateTime? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static DateTime GetClaimSortTime(MemoryClaim claim) =>
        claim.EffectiveAt ?? claim.RecordedAt;

    private static bool IsSameResolvedValue(
        MemoryClaim claim,
        string? objectEntityId,
        string? normalizedValueText,
        string? normalizedValueJson)
    {
        return string.Equals(claim.ObjectEntityId, objectEntityId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeOptionalText(claim.ValueText), normalizedValueText, StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeOptionalText(claim.ValueJson), normalizedValueJson, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSupersededClaimId(string? rawReference, IReadOnlyDictionary<string, string> claimIdByInputRef)
    {
        if (string.IsNullOrWhiteSpace(rawReference))
            return null;

        return claimIdByInputRef.GetValueOrDefault(rawReference, MemoryNormalizationService.ToSnakeCase(rawReference));
    }

    private static string ComputeHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HumanizeId(string value)
    {
        return string.Join(' ', value
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
