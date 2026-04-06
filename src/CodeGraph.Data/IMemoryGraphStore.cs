using CodeGraph.Models.Memory;

namespace CodeGraph.Data;

public interface IMemoryGraphStore
{
    Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities);
    Task AddRelationshipsBatchAsync(IReadOnlyList<MemoryRelationship> relationships);
    Task CreateObservationAsync(MemoryObservation obs, string? fromEntityId = null, string? toEntityId = null);
    Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId);

    Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, int topK = 5);
    Task<List<MemoryEntity>> TextSearchAsync(string query, int limit = 5);
    Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId);
    Task<(List<MemoryEntity> Entities, List<MemoryRelationshipDetail> Relationships)> GetSubgraphAsync(
        IReadOnlyList<string> seedIds, int hops = 2, int maxNodes = 20, int maxRelsPerPair = 5);
    Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds);
    Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0);
    Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, int limit = 20);
    Task<MemoryEntity?> GetEntityAsync(string entityId);
}
