using CodeGraph.Api.Auth;
using CodeGraph.Memory.Client;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;

namespace CodeGraph.Api.Memory;

public sealed class RemoteMemoryOperationsService(
    IMemoryClient memoryClient,
    IHttpContextAccessor httpContextAccessor) : IMemoryOperationsService
{
    public Task<MemoryStoreAcceptedResult> QueueClaimsAsync(
        MemoryClaimExtractionResult extraction,
        string source,
        string inputMode,
        CancellationToken ct = default)
        => memoryClient.QueueClaimsAsync(ResolveUsername(), extraction, source, ct);

    public Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId, CancellationToken ct = default)
        => memoryClient.GetWriteStatusAsync(ResolveUsername(), receiptId, ct);

    public Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default)
        => memoryClient.GetWriteDiagnosticsAsync(ResolveUsername(), staleAfterMinutes, sampleLimit, ct);

    public Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default)
        => memoryClient.GetDiagnosticsAsync(ResolveUsername(), staleAfterMinutes, sampleLimit, ct);

    public Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(
        string source,
        bool dryRun,
        CancellationToken ct = default)
        => memoryClient.DeleteBySourceAsync(ResolveUsername(), source, dryRun, ct);

    public Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(bool dryRun, CancellationToken ct = default)
        => memoryClient.DeleteTestDataAsync(ResolveUsername(), dryRun, ct);

    public Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default)
        => memoryClient.DeleteByIdsAsync(ResolveUsername(), claimIds, entityIds, dryRun, ct);

    public Task<MemorySearchResult> SearchMemoryAsync(
        string query,
        int entityLimit = 5,
        int claimLimit = 5,
        CancellationToken ct = default)
        => memoryClient.SearchAsync(ResolveUsername(), query, entityLimit, claimLimit, ct);

    public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphRequest request, CancellationToken ct = default)
        => memoryClient.GetSubgraphAsync(ResolveUsername(), request, ct);

    public Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20,
        CancellationToken ct = default)
        => memoryClient.GetEntityBundleAsync(
            ResolveUsername(),
            entityId,
            includeSuperseded,
            includeConflicts,
            neighborLimit,
            ct);

    public async Task<MemoryGraphSnapshot> GetEntityGraphAsync(
        string entityId,
        int neighborLimit = 200,
        CancellationToken ct = default)
    {
        return await memoryClient.GetEntityGraphAsync(ResolveUsername(), entityId, neighborLimit, ct)
               ?? new MemoryGraphSnapshot();
    }

    public Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true,
        CancellationToken ct = default)
        => memoryClient.GetClaimBundleAsync(
            ResolveUsername(),
            claimId,
            includeSupersessionChain,
            includeConflicts,
            includeEvidence,
            ct);

    public Task<MemoryFrontierExpansionResult> ExpandMemoryFrontierAsync(
        MemoryFrontierExpansionRequest request,
        CancellationToken ct = default)
        => memoryClient.ExpandFrontierAsync(ResolveUsername(), request, ct);

    public Task<MemorySummaryRenderResult> RenderMemorySummaryAsync(
        MemorySummaryRenderRequest request,
        CancellationToken ct = default)
        => memoryClient.RenderSummaryAsync(ResolveUsername(), request, ct);

    public Task<MemoryQueryResult> QueryAsync(
        string topic,
        int hops = 2,
        int maxNodes = 20,
        CancellationToken ct = default)
        => memoryClient.QueryAsync(ResolveUsername(), topic, hops, maxNodes, ct);

    public Task<MemoryGraphSnapshot> GetFullGraphAsync(
        int limit = 200,
        int skip = 0,
        CancellationToken ct = default)
        => memoryClient.GetGraphAsync(ResolveUsername(), limit, skip, ct);

    public async Task<MemoryEntityWithRelationships?> GetEntityWithRelationshipsAsync(
        string entityId,
        CancellationToken ct = default)
    {
        var response = await memoryClient.GetEntityWithRelationshipsAsync(ResolveUsername(), entityId, ct);
        return response is null
            ? null
            : new MemoryEntityWithRelationships
            {
                Entity = response.Entity,
                Relationships = response.Relationships.ToList(),
                VectorScore = 0,
            };
    }

    private string ResolveUsername()
    {
        var user = httpContextAccessor.HttpContext?.User;
        return user?.GetUsername()?.Trim().ToLowerInvariant() is { Length: > 0 } username
            ? username
            : "system";
    }
}
