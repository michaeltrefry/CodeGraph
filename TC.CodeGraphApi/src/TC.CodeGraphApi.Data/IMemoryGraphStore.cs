using TC.CodeGraphApi.Models.Memory;

namespace TC.CodeGraphApi.Data;

public interface IMemoryGraphStore
{
    Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities);
    Task AddRelationshipsBatchAsync(IReadOnlyList<MemoryRelationship> relationships, string username);
    Task CreateObservationAsync(MemoryObservation obs, string? fromEntityId = null, string? toEntityId = null);
    Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId);

    Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, string username, int topK = 5);
    Task<List<MemoryEntity>> TextSearchAsync(string query, string username, int limit = 5);
    Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId, string username);
    Task<(List<MemoryEntity> Entities, List<MemoryRelationshipDetail> Relationships)> GetSubgraphAsync(
        IReadOnlyList<string> seedIds, string username, int hops = 2, int maxNodes = 20, int maxRelsPerPair = 5);
    Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds, string username);
    Task<MemoryGraphSnapshot> GetFullGraphAsync(string username, int limit = 200, int skip = 0);
    Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, string username, int limit = 20);
    Task<MemoryEntity?> GetEntityAsync(string entityId, string username);
}
