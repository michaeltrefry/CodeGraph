using CodeGraph.Models.Memory;

namespace CodeGraph.Data;

public interface IMemoryGraphStore
{
    Task CreateWriteReceiptAsync(MemoryWriteReceipt receipt);
    Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId);
    Task UpdateWriteReceiptStatusAsync(string receiptId, MemoryWriteReceiptStatus status, StoreMemoryResult? result = null,
        string? errorMessage = null);
    Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities);
    Task UpsertClaimsBatchAsync(IReadOnlyList<MemoryClaim> claims);
    Task AddClaimEdgesBatchAsync(IReadOnlyList<MemoryClaimEdge> edges);
    Task AddEvidenceBatchAsync(IReadOnlyList<MemoryEvidence> evidence);
    Task UpsertEntityEdgesBatchAsync(IReadOnlyList<MemoryEntityEdge> edges);
    Task CreateObservationAsync(MemoryObservation obs);
    Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId);

    Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, int topK = 5);
    Task<List<MemoryEntity>> TextSearchAsync(string query, int limit = 5);
    Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId);
    Task<List<MemoryObservation>> GetUnresolvedObservationsAsync(IEnumerable<string> entityIds, IEnumerable<string> claimIds);
    Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds);
    Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0);
    Task<MemoryGraphSnapshot> GetEntityGraphAsync(string entityId, int neighborLimit = 200);
    Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, int limit = 20);
    Task<MemoryEntity?> GetEntityAsync(string entityId);
    Task<MemoryEntity?> GetEntityByExternalIdAsync(string externalId);
    Task<List<MemoryLegacyRelationship>> GetLegacyRelationshipsAsync();
    Task<List<MemoryObservation>> GetAllObservationsAsync();
    Task<MemoryClaim?> GetClaimAsync(string claimId);
    Task<List<MemoryClaim>> GetClaimsByFactGroupAsync(string factGroupKey);
    Task<List<MemoryClaim>> GetClaimsBySubjectPredicateAsync(string subjectEntityId, string predicate);
    Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20);
    Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true);
    Task<List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchClaimsAsync(
        string query,
        float[]? queryEmbedding,
        int limit = 5,
        bool includeSuperseded = false);
    Task<MemorySubgraphResult> GetMemorySubgraphAsync(
        MemorySubgraphQuery query,
        int maxHops = 2,
        int maxReturnedEntities = 20,
        int maxReturnedClaims = 40,
        bool includeSuperseded = false,
        bool includeConflicts = true);
}
