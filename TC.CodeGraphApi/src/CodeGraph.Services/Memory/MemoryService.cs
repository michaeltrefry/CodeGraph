using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models.Memory;

namespace CodeGraph.Services.Memory;

public class MemoryService
{
    private readonly MemoryNormalizationService _normalization;
    private readonly MemoryRetrievalService _retrieval;
    private readonly IMemoryGraphStore _store;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(
        MemoryNormalizationService normalization,
        MemoryRetrievalService retrieval,
        IMemoryGraphStore store,
        ILogger<MemoryService> logger)
    {
        _normalization = normalization;
        _retrieval = retrieval;
        _store = store;
        _logger = logger;
    }

    public async Task<StoreMemoryResult> StoreStructuredAsync(string username, MemoryExtractionResult extraction, string source = "api")
    {
        return await _normalization.NormalizeAndUpsertAsync(extraction, source, username);
    }

    public async Task<MemoryQueryResult> QueryAsync(string username, string topic, int hops = 2, int maxNodes = 20)
    {
        return await _retrieval.QueryAsync(topic, username, hops, maxNodes);
    }

    public async Task<MemoryEntity?> GetEntityAsync(string username, string entityId)
    {
        return await _store.GetEntityAsync(entityId, username);
    }

    public async Task<List<MemoryRelationshipDetail>> GetEntityRelationshipsAsync(string username, string entityId)
    {
        return await _store.GetRelationshipsAsync(entityId, username);
    }

    public async Task<MemoryGraphSnapshot> GetFullGraphAsync(string username, int limit = 200, int skip = 0)
    {
        return await _store.GetFullGraphAsync(username, limit, skip);
    }
}
