using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Memory;

public class MemoryLegacyMigrationServiceTests
{
    [Fact]
    public async Task MigrateAsync_ReplaysLegacyRelationshipsChronologically()
    {
        var store = new FakeMemoryGraphStore();
        store.Entities["michael"] = new MemoryEntity
        {
            Id = "michael",
            ExternalId = "michael",
            Label = "Michael",
            Type = "person",
            Summary = "Maintainer",
            Source = "test",
        };
        store.Entities["memory_system"] = new MemoryEntity
        {
            Id = "memory_system",
            ExternalId = "memory_system",
            Label = "Memory System",
            Type = "concept",
            Summary = "Claim graph",
            Source = "test",
        };
        store.LegacyRelationships.AddRange(
        [
            new MemoryLegacyRelationship
            {
                FromEntityId = "michael",
                ToEntityId = "memory_system",
                RelationshipType = "uses",
                Context = "initial adoption",
                Source = "legacy-thread",
                Timestamp = new DateTime(2026, 4, 9, 10, 0, 0, DateTimeKind.Utc),
            },
            new MemoryLegacyRelationship
            {
                FromEntityId = "michael",
                ToEntityId = "memory_system",
                RelationshipType = "uses",
                Context = "current standard approach",
                Source = "legacy-thread",
                Timestamp = new DateTime(2026, 4, 10, 10, 0, 0, DateTimeKind.Utc),
            }
        ]);

        var ingestion = new MemoryClaimIngestionService(store, new FakeEmbeddingService(), NullLogger<MemoryClaimIngestionService>.Instance);
        var service = new MemoryLegacyMigrationService(store, ingestion, NullLogger<MemoryLegacyMigrationService>.Instance);

        var result = await service.MigrateAsync();

        result.LegacyRelationshipsRead.ShouldBe(2);
        result.LegacyRelationshipsSkipped.ShouldBe(0);
        result.StoreResult.ClaimsWritten.ShouldBe(3);
        store.Claims.Count.ShouldBe(2);
        store.Claims.Values.Count(claim => claim.Status == MemoryClaimStatus.Active).ShouldBe(1);
        store.Claims.Values.Count(claim => claim.Status == MemoryClaimStatus.Superseded).ShouldBe(1);
        store.Evidence.Count.ShouldBe(2);
        store.EntityEdges.ShouldContain(edge =>
            edge.FromEntityId == "michael"
            && edge.ToEntityId == "memory_system"
            && edge.EdgeType == "uses");
    }

    [Fact]
    public async Task MigrateAsync_SkipsRelationshipsAlreadyMigratedByLegacyClaimId()
    {
        var store = new FakeMemoryGraphStore();
        store.Entities["michael"] = new MemoryEntity
        {
            Id = "michael",
            ExternalId = "michael",
            Label = "Michael",
            Type = "person",
            Summary = "Maintainer",
            Source = "test",
        };
        store.Entities["memory_system"] = new MemoryEntity
        {
            Id = "memory_system",
            ExternalId = "memory_system",
            Label = "Memory System",
            Type = "concept",
            Summary = "Claim graph",
            Source = "test",
        };

        var relationship = new MemoryLegacyRelationship
        {
            FromEntityId = "michael",
            ToEntityId = "memory_system",
            RelationshipType = "uses",
            Source = "legacy-thread",
            Timestamp = new DateTime(2026, 4, 10, 10, 0, 0, DateTimeKind.Utc),
        };
        store.LegacyRelationships.Add(relationship);

        var ingestion = new MemoryClaimIngestionService(store, new FakeEmbeddingService(), NullLogger<MemoryClaimIngestionService>.Instance);
        var service = new MemoryLegacyMigrationService(store, ingestion, NullLogger<MemoryLegacyMigrationService>.Instance);

        await service.MigrateAsync();
        var rerun = await service.MigrateAsync();

        rerun.LegacyRelationshipsRead.ShouldBe(1);
        rerun.LegacyRelationshipsSkipped.ShouldBe(1);
        rerun.StoreResult.ClaimsWritten.ShouldBe(0);
        store.Claims.Count.ShouldBe(1);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public bool IsAvailable => false;
        public int Dimensions => 0;
        public float[] GenerateEmbedding(string text) => [];
        public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts) => [];
    }

    private sealed class FakeMemoryGraphStore : IMemoryGraphStore
    {
        public Dictionary<string, MemoryEntity> Entities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MemoryClaim> Claims { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<MemoryClaimEdge> ClaimEdges { get; } = [];
        public List<MemoryEntityEdge> EntityEdges { get; } = [];
        public List<MemoryEvidence> Evidence { get; } = [];
        public List<MemoryObservation> Observations { get; } = [];
        public List<MemoryLegacyRelationship> LegacyRelationships { get; } = [];
        public Dictionary<string, MemoryWriteReceipt> WriteReceipts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task CreateWriteReceiptAsync(MemoryWriteReceipt receipt)
        {
            WriteReceipts[receipt.Id] = receipt;
            return Task.CompletedTask;
        }

        public Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId) =>
            Task.FromResult(WriteReceipts.GetValueOrDefault(receiptId));

        public Task UpdateWriteReceiptStatusAsync(string receiptId, MemoryWriteReceiptStatus status, StoreMemoryResult? result = null,
            string? errorMessage = null) => Task.CompletedTask;

        public Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities)
        {
            foreach (var entity in entities)
                Entities[entity.Id] = entity;
            return Task.CompletedTask;
        }

        public Task UpsertClaimsBatchAsync(IReadOnlyList<MemoryClaim> claims)
        {
            foreach (var claim in claims)
                Claims[claim.Id] = claim;
            return Task.CompletedTask;
        }

        public Task AddClaimEdgesBatchAsync(IReadOnlyList<MemoryClaimEdge> edges)
        {
            ClaimEdges.AddRange(edges);
            return Task.CompletedTask;
        }

        public Task AddEvidenceBatchAsync(IReadOnlyList<MemoryEvidence> evidence)
        {
            Evidence.AddRange(evidence);
            return Task.CompletedTask;
        }

        public Task UpsertEntityEdgesBatchAsync(IReadOnlyList<MemoryEntityEdge> edges)
        {
            foreach (var edge in edges)
            {
                EntityEdges.RemoveAll(existing =>
                    existing.FromEntityId.Equals(edge.FromEntityId, StringComparison.OrdinalIgnoreCase)
                    && existing.ToEntityId.Equals(edge.ToEntityId, StringComparison.OrdinalIgnoreCase)
                    && existing.EdgeType.Equals(edge.EdgeType, StringComparison.OrdinalIgnoreCase));
                EntityEdges.Add(edge);
            }

            return Task.CompletedTask;
        }

        public Task CreateObservationAsync(MemoryObservation obs)
        {
            Observations.Add(obs);
            return Task.CompletedTask;
        }

        public Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId) => Task.CompletedTask;
        public Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, int topK = 5) => Task.FromResult(new List<(MemoryEntity Entity, double Score)>());
        public Task<List<MemoryEntity>> TextSearchAsync(string query, int limit = 5) => Task.FromResult(new List<MemoryEntity>());
        public Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId) => Task.FromResult(new List<MemoryRelationshipDetail>());
        public Task<List<MemoryObservation>> GetUnresolvedObservationsAsync(IEnumerable<string> entityIds, IEnumerable<string> claimIds) => Task.FromResult(new List<MemoryObservation>());
        public Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds) => Task.FromResult(new List<MemoryObservation>());
        public Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0) => Task.FromResult(new MemoryGraphSnapshot());
        public Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, int limit = 20) => Task.FromResult(new List<string>());
        public Task<MemoryEntity?> GetEntityAsync(string entityId) => Task.FromResult(Entities.GetValueOrDefault(entityId));
        public Task<MemoryEntity?> GetEntityByExternalIdAsync(string externalId) => Task.FromResult(Entities.Values.FirstOrDefault(entity =>
            string.Equals(entity.ExternalId, externalId, StringComparison.OrdinalIgnoreCase)));
        public Task<List<MemoryLegacyRelationship>> GetLegacyRelationshipsAsync() => Task.FromResult(LegacyRelationships.ToList());
        public Task<List<MemoryObservation>> GetAllObservationsAsync() => Task.FromResult(Observations.ToList());
        public Task<MemoryClaim?> GetClaimAsync(string claimId) => Task.FromResult(Claims.GetValueOrDefault(claimId));
        public Task<List<MemoryClaim>> GetClaimsByFactGroupAsync(string factGroupKey) => Task.FromResult(Claims.Values
            .Where(claim => claim.FactGroupKey.Equals(factGroupKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(claim => claim.RecordedAt)
            .ToList());
        public Task<List<MemoryClaim>> GetClaimsBySubjectPredicateAsync(string subjectEntityId, string predicate) => Task.FromResult(Claims.Values
            .Where(claim => claim.SubjectEntityId.Equals(subjectEntityId, StringComparison.OrdinalIgnoreCase)
                && claim.Predicate.Equals(predicate, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(claim => claim.RecordedAt)
            .ToList());
        public Task<MemoryEntityBundle?> GetEntityBundleAsync(string entityId, bool includeSuperseded = false, bool includeConflicts = true, int neighborLimit = 20)
            => Task.FromResult<MemoryEntityBundle?>(null);
        public Task<MemoryClaimBundle?> GetClaimBundleAsync(string claimId, bool includeSupersessionChain = true, bool includeConflicts = true, bool includeEvidence = true)
            => Task.FromResult<MemoryClaimBundle?>(null);
        public Task<List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchClaimsAsync(string query, float[]? queryEmbedding, int limit = 5, bool includeSuperseded = false)
            => Task.FromResult(new List<(MemoryClaim Claim, double Score, string MatchKind)>());
        public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphQuery query, int maxHops = 2, int maxReturnedEntities = 20, int maxReturnedClaims = 40, bool includeSuperseded = false, bool includeConflicts = true)
            => Task.FromResult(new MemorySubgraphResult());
    }
}
