using CodeGraph.Models.Memory;
using CodeGraph.Services.Messaging;

namespace CodeGraph.Services.Memory;

public sealed class LocalMemoryOperationsService(
    MemoryService memoryService,
    IMessageBus messageBus) : IMemoryOperationsService
{
    public Task<MemoryStoreAcceptedResult> QueueClaimsAsync(
        MemoryClaimExtractionResult extraction,
        string source,
        string inputMode,
        CancellationToken ct = default)
        => memoryService.QueueClaimsAsync(extraction, source, inputMode, messageBus, ct);

    public Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId, CancellationToken ct = default)
        => memoryService.GetWriteReceiptAsync(receiptId);

    public Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default)
        => memoryService.GetWriteDiagnosticsAsync(staleAfterMinutes, sampleLimit);

    public Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default)
        => memoryService.GetDiagnosticsAsync(staleAfterMinutes, sampleLimit);

    public Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(
        string source,
        bool dryRun,
        CancellationToken ct = default)
        => memoryService.DeleteMemoryBySourceAsync(source, dryRun, ct);

    public Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(bool dryRun, CancellationToken ct = default)
        => memoryService.DeleteMemoryTestDataAsync(dryRun, ct);

    public Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default)
        => memoryService.DeleteMemoryByIdsAsync(claimIds, entityIds, dryRun, ct);

    public Task<MemorySearchResult> SearchMemoryAsync(
        string query,
        int entityLimit = 5,
        int claimLimit = 5,
        CancellationToken ct = default)
        => memoryService.SearchMemoryAsync(query, entityLimit, claimLimit);

    public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphRequest request, CancellationToken ct = default)
        => memoryService.GetMemorySubgraphAsync(request);

    public Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20,
        CancellationToken ct = default)
        => memoryService.GetEntityBundleAsync(entityId, includeSuperseded, includeConflicts, neighborLimit);

    public Task<MemoryGraphSnapshot> GetEntityGraphAsync(
        string entityId,
        int neighborLimit = 200,
        CancellationToken ct = default)
        => memoryService.GetEntityGraphAsync(entityId, neighborLimit);

    public Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true,
        CancellationToken ct = default)
        => memoryService.GetClaimBundleAsync(claimId, includeSupersessionChain, includeConflicts, includeEvidence);

    public Task<MemoryFrontierExpansionResult> ExpandMemoryFrontierAsync(
        MemoryFrontierExpansionRequest request,
        CancellationToken ct = default)
        => memoryService.ExpandMemoryFrontierAsync(request);

    public Task<MemorySummaryRenderResult> RenderMemorySummaryAsync(
        MemorySummaryRenderRequest request,
        CancellationToken ct = default)
        => memoryService.RenderMemorySummaryAsync(request);

    public Task<MemoryQueryResult> QueryAsync(
        string topic,
        int hops = 2,
        int maxNodes = 20,
        CancellationToken ct = default)
        => memoryService.QueryAsync(topic, hops, maxNodes);

    public Task<MemoryGraphSnapshot> GetFullGraphAsync(
        int limit = 200,
        int skip = 0,
        CancellationToken ct = default)
        => memoryService.GetFullGraphAsync(limit, skip);

    public async Task<MemoryEntityWithRelationships?> GetEntityWithRelationshipsAsync(
        string entityId,
        CancellationToken ct = default)
    {
        var entity = await memoryService.GetEntityAsync(entityId);
        if (entity is null)
            return null;

        var relationships = await memoryService.GetEntityRelationshipsAsync(entityId);
        return new MemoryEntityWithRelationships
        {
            Entity = entity,
            Relationships = relationships,
            VectorScore = 0,
        };
    }
}
