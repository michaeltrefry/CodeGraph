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

    public async Task<StoreMemoryResult> StoreStructuredAsync(MemoryExtractionResult extraction, string source = "api")
    {
        return await _normalization.NormalizeAndUpsertAsync(extraction, source);
    }

    public async Task<MemoryQueryResult> QueryAsync(string topic, int hops = 2, int maxNodes = 20)
    {
        return await _retrieval.QueryAsync(topic, hops, maxNodes);
    }

    public async Task<MemoryEntity?> GetEntityAsync(string entityId)
    {
        return await _store.GetEntityAsync(entityId);
    }

    public async Task<List<MemoryRelationshipDetail>> GetEntityRelationshipsAsync(string entityId)
    {
        return await _store.GetRelationshipsAsync(entityId);
    }

    public async Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0)
    {
        return await _store.GetFullGraphAsync(limit, skip);
    }
}
