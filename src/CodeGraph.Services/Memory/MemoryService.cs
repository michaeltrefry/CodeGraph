using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Messaging;

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

    public async Task<MemoryStoreAcceptedResult> QueueClaimsAsync(
        MemoryClaimExtractionResult extraction,
        string source,
        string inputMode,
        IMessageBus messageBus,
        CancellationToken ct = default)
    {
        var receipt = new MemoryWriteReceipt
        {
            Id = $"memory_write_{Guid.NewGuid():N}",
            Source = source,
            InputMode = inputMode,
            Status = MemoryWriteReceiptStatus.Queued,
            EntitiesRequested = extraction.Entities.Count,
            ClaimsRequested = extraction.Claims.Count,
            EvidenceRequested = extraction.Evidence.Count,
        };

        await _store.CreateWriteReceiptAsync(receipt);

        try
        {
            await messageBus.PublishAsync(new StoreMemoryClaims
            {
                ReceiptId = receipt.Id,
                InputMode = inputMode,
                Extraction = extraction,
                Source = source,
            }, ct);
        }
        catch (Exception ex)
        {
            await _store.UpdateWriteReceiptStatusAsync(receipt.Id, MemoryWriteReceiptStatus.Failed, errorMessage: ex.Message);
            throw;
        }

        return new MemoryStoreAcceptedResult
        {
            Status = "queued",
            ReceiptId = receipt.Id,
            Source = source,
            InputMode = inputMode,
            EntitiesRequested = extraction.Entities.Count,
            ClaimsRequested = extraction.Claims.Count,
            EvidenceRequested = extraction.Evidence.Count,
        };
    }

    public async Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId)
    {
        return await _store.GetWriteReceiptAsync(receiptId);
    }

    public async Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        int staleAfterMinutes = 15,
        int sampleLimit = 10)
    {
        return await _store.GetWriteDiagnosticsAsync(staleAfterMinutes, sampleLimit);
    }

    public async Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(int staleAfterMinutes = 15, int sampleLimit = 10)
    {
        return await _store.GetDiagnosticsAsync(staleAfterMinutes, sampleLimit);
    }

    public async Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(
        string source,
        bool dryRun,
        CancellationToken ct = default)
    {
        return await _store.DeleteMemoryBySourceAsync(source, dryRun, ct);
    }

    public async Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(
        bool dryRun,
        CancellationToken ct = default)
    {
        return await _store.DeleteMemoryTestDataAsync(dryRun, ct);
    }

    public async Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default)
    {
        return await _store.DeleteMemoryByIdsAsync(claimIds, entityIds, dryRun, ct);
    }

    public async Task MarkWriteReceiptProcessingAsync(string receiptId)
    {
        await _store.UpdateWriteReceiptStatusAsync(receiptId, MemoryWriteReceiptStatus.Processing);
    }

    public async Task CompleteWriteReceiptAsync(string receiptId, StoreMemoryResult result)
    {
        await _store.UpdateWriteReceiptStatusAsync(receiptId, MemoryWriteReceiptStatus.Completed, result);
    }

    public async Task FailWriteReceiptAsync(string receiptId, string errorMessage)
    {
        await _store.UpdateWriteReceiptStatusAsync(receiptId, MemoryWriteReceiptStatus.Failed, errorMessage: errorMessage);
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

    public async Task<MemoryGraphSnapshot> GetEntityGraphAsync(string entityId, int neighborLimit = 200)
    {
        return await _store.GetEntityGraphAsync(entityId, neighborLimit);
    }
}
