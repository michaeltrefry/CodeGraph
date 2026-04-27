using CodeGraph.Models.Memory;

namespace CodeGraph.Memory.Client;

public interface IMemoryClient
{
    Task<MemoryStoreAcceptedResult> QueueClaimsAsync(
        string username,
        MemoryClaimExtractionResult extraction,
        string source = "api",
        CancellationToken ct = default);

    Task<MemoryWriteReceipt?> GetWriteStatusAsync(string username, string receiptId, CancellationToken ct = default);

    Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        string username,
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default);

    Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
        string username,
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteBySourceAsync(
        string username,
        string source,
        bool dryRun,
        CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteTestDataAsync(
        string username,
        bool dryRun,
        CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteByIdsAsync(
        string username,
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default);

    Task<MemorySearchResult> SearchAsync(
        string username,
        string query,
        int entityLimit = 5,
        int claimLimit = 5,
        CancellationToken ct = default);

    Task<MemorySubgraphResult> GetSubgraphAsync(
        string username,
        MemorySubgraphRequest request,
        CancellationToken ct = default);

    Task<MemoryFrontierExpansionResult> ExpandFrontierAsync(
        string username,
        MemoryFrontierExpansionRequest request,
        CancellationToken ct = default);

    Task<MemorySummaryRenderResult> RenderSummaryAsync(
        string username,
        MemorySummaryRenderRequest request,
        CancellationToken ct = default);

    Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string username,
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20,
        CancellationToken ct = default);

    Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string username,
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true,
        CancellationToken ct = default);

    Task<MemoryQueryResult> QueryAsync(
        string username,
        string topic,
        int hops = 2,
        int maxNodes = 20,
        CancellationToken ct = default);

    Task<MemoryGraphSnapshot> GetGraphAsync(
        string username,
        int limit = 200,
        int skip = 0,
        CancellationToken ct = default);

    Task<MemoryGraphSnapshot?> GetEntityGraphAsync(
        string username,
        string entityId,
        int neighborLimit = 200,
        CancellationToken ct = default);

    Task<MemoryEntityWithRelationshipsResponse?> GetEntityWithRelationshipsAsync(
        string username,
        string entityId,
        CancellationToken ct = default);

}
