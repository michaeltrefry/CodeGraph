using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Memory;

public class MemoryLegacyMigrationService(
    IMemoryGraphStore store,
    MemoryClaimIngestionService claimIngestion,
    ILogger<MemoryLegacyMigrationService> logger)
{
    public const string MigrationSource = "legacy_relates_to_migration";

    public async Task<MemoryLegacyMigrationResult> MigrateAsync()
    {
        var legacyRelationships = (await store.GetLegacyRelationshipsAsync())
            .OrderBy(relationship => relationship.Timestamp)
            .ThenBy(relationship => relationship.FromEntityId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relationship => relationship.RelationshipType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relationship => relationship.ToEntityId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new MemoryLegacyMigrationResult
        {
            MigrationSource = MigrationSource,
            LegacyRelationshipsRead = legacyRelationships.Count,
            LegacyRelationshipsWithContext = legacyRelationships.Count(relationship => !string.IsNullOrWhiteSpace(relationship.Context)),
        };

        foreach (var relationship in legacyRelationships)
        {
            var legacyClaimId = ComputeLegacyClaimId(relationship);
            if (await store.GetClaimAsync(legacyClaimId) != null)
            {
                result.LegacyRelationshipsSkipped++;
                continue;
            }

            var extraction = await BuildExtractionAsync(relationship, legacyClaimId);
            var batchResult = await claimIngestion.NormalizeAndUpsertClaimsAsync(extraction, MigrationSource);
            Accumulate(result.StoreResult, batchResult);

            if (batchResult.ClaimsWritten == 0)
                result.LegacyRelationshipsSkipped++;
        }

        logger.LogInformation(
            "Migrated {ReadCount} legacy RELATES_TO relationships into claim-centric memory ({ClaimCount} claims written, {SkippedCount} skipped)",
            result.LegacyRelationshipsRead,
            result.StoreResult.ClaimsWritten,
            result.LegacyRelationshipsSkipped);

        return result;
    }

    internal static string ComputeLegacyClaimId(MemoryLegacyRelationship relationship)
    {
        var hash = ComputeHash(
            $"{relationship.FromEntityId}|{relationship.RelationshipType}|{relationship.ToEntityId}|{relationship.Context}|{relationship.Source}|{relationship.Timestamp:O}|{relationship.Supersedes}");
        return $"legacy_claim_{hash[..24]}";
    }

    internal static string BuildBaseNormalizedText(MemoryLegacyRelationship relationship)
    {
        var subjectId = MemoryNormalizationService.ToSnakeCase(relationship.FromEntityId);
        var predicate = MemoryClaimIngestionService.NormalizePredicate(relationship.RelationshipType);
        var objectId = MemoryNormalizationService.ToSnakeCase(relationship.ToEntityId);
        var parts = new List<string> { subjectId, predicate, objectId };

        if (!string.IsNullOrWhiteSpace(relationship.Context))
            parts.Add(MemoryClaimIngestionService.NormalizeFreeText(relationship.Context));

        return string.Join(' ', parts);
    }

    private async Task<MemoryClaimExtractionResult> BuildExtractionAsync(
        MemoryLegacyRelationship relationship,
        string legacyClaimId)
    {
        var subjectId = MemoryNormalizationService.ToSnakeCase(relationship.FromEntityId);
        var predicate = MemoryClaimIngestionService.NormalizePredicate(relationship.RelationshipType);
        var objectId = MemoryNormalizationService.ToSnakeCase(relationship.ToEntityId);

        var normalizedText = BuildBaseNormalizedText(relationship);
        var factGroupKey = MemoryClaimIngestionService.ComputeFactGroupKey(subjectId, predicate, objectId, null, null);
        var existingFactGroupClaims = await store.GetClaimsByFactGroupAsync(factGroupKey);
        var claimKey = MemoryClaimIngestionService.ComputeClaimKey(
            subjectId,
            predicate,
            objectId,
            null,
            null,
            normalizedText);

        if (existingFactGroupClaims.Any(claim => claim.ClaimKey.Equals(claimKey, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedText = $"{normalizedText} legacy_ref {legacyClaimId[^8..]}";
        }

        return new MemoryClaimExtractionResult
        {
            Claims =
            [
                new MemoryExtractedClaim
                {
                    Id = legacyClaimId,
                    Subject = relationship.FromEntityId,
                    Predicate = relationship.RelationshipType,
                    Object = relationship.ToEntityId,
                    NormalizedText = normalizedText,
                    RecordedAt = relationship.Timestamp.ToString("O"),
                    Source = MigrationSource,
                }
            ],
            Evidence =
            [
                new MemoryExtractedEvidence
                {
                    ClaimId = legacyClaimId,
                    EvidenceType = "legacy_relationship",
                    SourceRef = string.IsNullOrWhiteSpace(relationship.Source) ? MigrationSource : relationship.Source!,
                    Snippet = relationship.Context,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        originalRelationshipType = relationship.RelationshipType,
                        originalSource = relationship.Source,
                        originalTimestamp = relationship.Timestamp,
                        legacySupersedes = relationship.Supersedes,
                        migratedFrom = "RELATES_TO",
                    }),
                }
            ],
        };
    }

    private static void Accumulate(StoreMemoryResult total, StoreMemoryResult batch)
    {
        total.NodesWritten += batch.NodesWritten;
        total.EdgesWritten += batch.EdgesWritten;
        total.ClaimsWritten += batch.ClaimsWritten;
        total.ConflictsDetected += batch.ConflictsDetected;
        total.EvidenceWritten += batch.EvidenceWritten;
        total.ObservationsWritten += batch.ObservationsWritten;
    }

    private static string ComputeHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
