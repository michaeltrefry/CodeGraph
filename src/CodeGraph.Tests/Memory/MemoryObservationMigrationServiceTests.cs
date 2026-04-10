using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Memory;

public class MemoryObservationMigrationServiceTests
{
    [Fact]
    public async Task MigrateAsync_AddsClaimAboutLinksAndPreservesEntityLinks()
    {
        var store = new FakeMemoryGraphStore();
        store.Observations.Add(new MemoryObservation
        {
            Id = "obs_1",
            Claim = "michael prefers clean slate design",
            ConflictsWith = "michael prefers incremental refactor",
            Source = "test",
            AboutEntityIds = ["michael"],
        });
        store.SearchResults["michael prefers clean slate design"] =
        [
            (
                new MemoryClaim
                {
                    Id = "claim_1",
                    ClaimKey = "claim_key_1",
                    FactGroupKey = "fact_group_1",
                    SubjectEntityId = "michael",
                    Predicate = "prefers",
                    ValueText = "clean slate design",
                    NormalizedText = "michael prefers clean slate design",
                    Source = "test",
                },
                100d,
                "lexical"
            )
        ];
        store.SearchResults["michael prefers incremental refactor"] =
        [
            (
                new MemoryClaim
                {
                    Id = "claim_2",
                    ClaimKey = "claim_key_2",
                    FactGroupKey = "fact_group_2",
                    SubjectEntityId = "michael",
                    Predicate = "prefers",
                    ValueText = "incremental refactor",
                    NormalizedText = "michael prefers incremental refactor",
                    Source = "test",
                },
                100d,
                "lexical"
            )
        ];

        var service = new MemoryObservationMigrationService(store, NullLogger<MemoryObservationMigrationService>.Instance);
        var result = await service.MigrateAsync();

        result.ObservationsRead.ShouldBe(1);
        result.ObservationsUpdated.ShouldBe(1);
        result.EntityLinksAdded.ShouldBe(0);
        result.ClaimLinksAdded.ShouldBe(2);
        store.UpsertedObservations.ShouldHaveSingleItem();
        store.UpsertedObservations[0].AboutEntityIds.ShouldBe(["michael"]);
        store.UpsertedObservations[0].AboutClaimIds.OrderBy(id => id).ShouldBe(["claim_1", "claim_2"]);
    }

    [Fact]
    public async Task MigrateAsync_SkipsObservationWhenNoNewLinksAreFound()
    {
        var store = new FakeMemoryGraphStore();
        store.Observations.Add(new MemoryObservation
        {
            Id = "obs_1",
            Claim = "michael prefers clean slate design",
            ConflictsWith = "michael prefers incremental refactor",
            Source = "test",
            AboutEntityIds = ["michael"],
            AboutClaimIds = ["claim_1", "claim_2"],
        });
        store.SearchResults["michael prefers clean slate design"] =
        [
            (
                new MemoryClaim
                {
                    Id = "claim_1",
                    ClaimKey = "claim_key_1",
                    FactGroupKey = "fact_group_1",
                    SubjectEntityId = "michael",
                    Predicate = "prefers",
                    NormalizedText = "michael prefers clean slate design",
                    Source = "test",
                },
                100d,
                "lexical"
            )
        ];
        store.SearchResults["michael prefers incremental refactor"] =
        [
            (
                new MemoryClaim
                {
                    Id = "claim_2",
                    ClaimKey = "claim_key_2",
                    FactGroupKey = "fact_group_2",
                    SubjectEntityId = "michael",
                    Predicate = "prefers",
                    NormalizedText = "michael prefers incremental refactor",
                    Source = "test",
                },
                100d,
                "lexical"
            )
        ];

        var service = new MemoryObservationMigrationService(store, NullLogger<MemoryObservationMigrationService>.Instance);
        var result = await service.MigrateAsync();

        result.ObservationsUpdated.ShouldBe(0);
        store.UpsertedObservations.ShouldBeEmpty();
    }

    private sealed class FakeMemoryGraphStore : IMemoryGraphStore
    {
        public List<MemoryObservation> Observations { get; } = [];
        public List<MemoryObservation> UpsertedObservations { get; } = [];
        public Dictionary<string, List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchResults { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities) => Task.CompletedTask;
        public Task UpsertClaimsBatchAsync(IReadOnlyList<MemoryClaim> claims) => Task.CompletedTask;
        public Task AddClaimEdgesBatchAsync(IReadOnlyList<MemoryClaimEdge> edges) => Task.CompletedTask;
        public Task AddEvidenceBatchAsync(IReadOnlyList<MemoryEvidence> evidence) => Task.CompletedTask;
        public Task UpsertEntityEdgesBatchAsync(IReadOnlyList<MemoryEntityEdge> edges) => Task.CompletedTask;
        public Task CreateObservationAsync(MemoryObservation obs)
        {
            UpsertedObservations.Add(new MemoryObservation
            {
                Id = obs.Id,
                Claim = obs.Claim,
                ConflictsWith = obs.ConflictsWith,
                Source = obs.Source,
                Timestamp = obs.Timestamp,
                Resolved = obs.Resolved,
                Resolution = obs.Resolution,
                ResolvedByMemoryId = obs.ResolvedByMemoryId,
                AboutEntityIds = obs.AboutEntityIds.ToList(),
                AboutClaimIds = obs.AboutClaimIds.ToList(),
            });
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
        public Task<MemoryEntity?> GetEntityAsync(string entityId) => Task.FromResult<MemoryEntity?>(null);
        public Task<MemoryEntity?> GetEntityByExternalIdAsync(string externalId) => Task.FromResult<MemoryEntity?>(null);
        public Task<List<MemoryLegacyRelationship>> GetLegacyRelationshipsAsync() => Task.FromResult(new List<MemoryLegacyRelationship>());
        public Task<List<MemoryObservation>> GetAllObservationsAsync() => Task.FromResult(Observations.ToList());
        public Task<MemoryClaim?> GetClaimAsync(string claimId) => Task.FromResult<MemoryClaim?>(null);
        public Task<List<MemoryClaim>> GetClaimsByFactGroupAsync(string factGroupKey) => Task.FromResult(new List<MemoryClaim>());
        public Task<List<MemoryClaim>> GetClaimsBySubjectPredicateAsync(string subjectEntityId, string predicate) => Task.FromResult(new List<MemoryClaim>());
        public Task<MemoryEntityBundle?> GetEntityBundleAsync(string entityId, bool includeSuperseded = false, bool includeConflicts = true, int neighborLimit = 20)
            => Task.FromResult<MemoryEntityBundle?>(null);
        public Task<MemoryClaimBundle?> GetClaimBundleAsync(string claimId, bool includeSupersessionChain = true, bool includeConflicts = true, bool includeEvidence = true)
            => Task.FromResult<MemoryClaimBundle?>(null);
        public Task<List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchClaimsAsync(string query, float[]? queryEmbedding, int limit = 5, bool includeSuperseded = false)
            => Task.FromResult(SearchResults.GetValueOrDefault(query, []).Take(limit).ToList());
        public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphQuery query, int maxHops = 2, int maxReturnedEntities = 20, int maxReturnedClaims = 40, bool includeSuperseded = false, bool includeConflicts = true)
            => Task.FromResult(new MemorySubgraphResult());
    }
}
