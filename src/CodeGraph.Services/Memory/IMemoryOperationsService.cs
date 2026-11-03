using CodeGraph.Models.Memory;

namespace CodeGraph.Services.Memory;

// projectKey: the key of a work project (CG-156). On a write it scopes what is written to that
// project; on a read it limits the answer to what is scoped to it. Null leaves both unscoped.
public interface IMemoryOperationsService
{
    Task<MemoryStoreAcceptedResult> QueueClaimsAsync(
        MemoryClaimExtractionResult extraction,
        string source,
        string inputMode,
        string? projectKey = null,
        CancellationToken ct = default);

    Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId, CancellationToken ct = default);

    Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default);

    Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(string source, bool dryRun, CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(bool dryRun, CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default);

    Task<MemorySearchResult> SearchMemoryAsync(
        string query,
        int entityLimit = 5,
        int claimLimit = 5,
        string? projectKey = null,
        CancellationToken ct = default);

    Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphRequest request, CancellationToken ct = default);

    Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20,
        string? projectKey = null,
        CancellationToken ct = default);

    Task<MemoryGraphSnapshot> GetEntityGraphAsync(
        string entityId,
        int neighborLimit = 200,
        string? projectKey = null,
        CancellationToken ct = default);

    Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true,
        string? projectKey = null,
        CancellationToken ct = default);

    Task<MemoryFrontierExpansionResult> ExpandMemoryFrontierAsync(
        MemoryFrontierExpansionRequest request,
        CancellationToken ct = default);

    Task<MemorySummaryRenderResult> RenderMemorySummaryAsync(
        MemorySummaryRenderRequest request,
        CancellationToken ct = default);

    // Subgraph, frontier expansion and summary requests carry their project as ProjectKey.
    Task<MemoryQueryResult> QueryAsync(
        string topic,
        int hops = 2,
        int maxNodes = 20,
        string? projectKey = null,
        CancellationToken ct = default);

    Task<MemoryGraphSnapshot> GetFullGraphAsync(
        int limit = 200,
        int skip = 0,
        string? projectKey = null,
        CancellationToken ct = default);

    Task<MemoryEntityWithRelationships?> GetEntityWithRelationshipsAsync(string entityId, CancellationToken ct = default);
}
