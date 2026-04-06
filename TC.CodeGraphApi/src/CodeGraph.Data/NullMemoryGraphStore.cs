using CodeGraph.Models.Memory;

namespace CodeGraph.Data;

/// <summary>
/// No-op implementation for when Neo4j is not configured.
/// Memory graph features require Neo4j.
/// </summary>
public class NullMemoryGraphStore : IMemoryGraphStore
{
    public Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities) => Task.CompletedTask;
    public Task AddRelationshipsBatchAsync(IReadOnlyList<MemoryRelationship> relationships, string username) => Task.CompletedTask;
    public Task CreateObservationAsync(MemoryObservation obs, string? fromEntityId = null, string? toEntityId = null) => Task.CompletedTask;
    public Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId) => Task.CompletedTask;
    public Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, string username, int topK = 5) => Task.FromResult(new List<(MemoryEntity, double)>());
    public Task<List<MemoryEntity>> TextSearchAsync(string query, string username, int limit = 5) => Task.FromResult(new List<MemoryEntity>());
    public Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId, string username) => Task.FromResult(new List<MemoryRelationshipDetail>());
    public Task<(List<MemoryEntity> Entities, List<MemoryRelationshipDetail> Relationships)> GetSubgraphAsync(
        IReadOnlyList<string> seedIds, string username, int hops = 2, int maxNodes = 20, int maxRelsPerPair = 5)
        => Task.FromResult<(List<MemoryEntity>, List<MemoryRelationshipDetail>)>(([], []));
    public Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds, string username) => Task.FromResult(new List<MemoryObservation>());
    public Task<MemoryGraphSnapshot> GetFullGraphAsync(string username, int limit = 200, int skip = 0) => Task.FromResult(new MemoryGraphSnapshot());
    public Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, string username, int limit = 20) => Task.FromResult(new List<string>());
    public Task<MemoryEntity?> GetEntityAsync(string entityId, string username) => Task.FromResult<MemoryEntity?>(null);
}
