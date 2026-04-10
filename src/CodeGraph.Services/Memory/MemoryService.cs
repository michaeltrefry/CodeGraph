using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models.Memory;

namespace CodeGraph.Services.Memory;

public class MemoryService
{
    private readonly MemoryClaimIngestionService _claimIngestion;
    private readonly MemoryLegacyMigrationService _legacyMigration;
    private readonly MemoryObservationMigrationService _observationMigration;
    private readonly MemoryRetrievalService _retrieval;
    private readonly IMemoryGraphStore _store;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(
        MemoryClaimIngestionService claimIngestion,
        MemoryLegacyMigrationService legacyMigration,
        MemoryObservationMigrationService observationMigration,
        MemoryRetrievalService retrieval,
        IMemoryGraphStore store,
        ILogger<MemoryService> logger)
    {
        _claimIngestion = claimIngestion;
        _legacyMigration = legacyMigration;
        _observationMigration = observationMigration;
        _retrieval = retrieval;
        _store = store;
        _logger = logger;
    }

    public async Task<StoreMemoryResult> StoreClaimsAsync(MemoryClaimExtractionResult extraction, string source = "api")
    {
        return await _claimIngestion.NormalizeAndUpsertClaimsAsync(extraction, source);
    }

    public async Task<MemoryLegacyMigrationResult> MigrateLegacyRelationshipsAsync()
    {
        return await _legacyMigration.MigrateAsync();
    }

    public async Task<MemoryObservationMigrationResult> MigrateObservationsAsync()
    {
        return await _observationMigration.MigrateAsync();
    }

    public async Task<MemoryQueryResult> QueryAsync(string topic, int hops = 2, int maxNodes = 20)
    {
        return await _retrieval.QueryAsync(topic, hops, maxNodes);
    }

    public async Task<MemorySearchResult> SearchMemoryAsync(string query, int entityLimit = 5, int claimLimit = 5)
    {
        return await _retrieval.SearchAsync(query, entityLimit, claimLimit);
    }

    public async Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphRequest request)
    {
        return await _retrieval.GetMemorySubgraphAsync(request);
    }

    public async Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20)
    {
        return await _retrieval.GetEntityBundleAsync(entityId, includeSuperseded, includeConflicts, neighborLimit);
    }

    public async Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true)
    {
        return await _retrieval.GetClaimBundleAsync(claimId, includeSupersessionChain, includeConflicts, includeEvidence);
    }

    public async Task<MemoryFrontierExpansionResult> ExpandMemoryFrontierAsync(MemoryFrontierExpansionRequest request)
    {
        return await _retrieval.ExpandMemoryFrontierAsync(request);
    }

    public async Task<MemorySummaryRenderResult> RenderMemorySummaryAsync(MemorySummaryRenderRequest request)
    {
        return await _retrieval.RenderMemorySummaryAsync(request);
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
