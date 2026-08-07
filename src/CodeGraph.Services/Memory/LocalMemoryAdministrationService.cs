using CodeGraph.Data;
using CodeGraph.Models.Memory;

namespace CodeGraph.Services.Memory;

public sealed class LocalMemoryAdministrationService(
    MemoryService memoryService,
    IMemoryTenantContext tenantContext) : IMemoryAdministrationService
{
    public Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        string targetUsername,
        int staleAfterMinutes,
        int sampleLimit,
        CancellationToken ct = default)
        => InTargetScopeAsync(
            targetUsername,
            () => memoryService.GetWriteDiagnosticsAsync(staleAfterMinutes, sampleLimit));

    public Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
        string targetUsername,
        int staleAfterMinutes,
        int sampleLimit,
        CancellationToken ct = default)
        => InTargetScopeAsync(
            targetUsername,
            () => memoryService.GetDiagnosticsAsync(staleAfterMinutes, sampleLimit));

    public Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(
        string targetUsername,
        string source,
        bool dryRun,
        CancellationToken ct = default)
        => InTargetScopeAsync(
            targetUsername,
            () => memoryService.DeleteMemoryBySourceAsync(source, dryRun, ct));

    public Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(
        string targetUsername,
        bool dryRun,
        CancellationToken ct = default)
        => InTargetScopeAsync(
            targetUsername,
            () => memoryService.DeleteMemoryTestDataAsync(dryRun, ct));

    public Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
        string targetUsername,
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default)
        => InTargetScopeAsync(
            targetUsername,
            () => memoryService.DeleteMemoryByIdsAsync(claimIds, entityIds, dryRun, ct));

    private async Task<T> InTargetScopeAsync<T>(string targetUsername, Func<Task<T>> operation)
    {
        var normalized = MemoryTenantContext.NormalizeAdministrativeTarget(targetUsername);
        using var tenantScope = tenantContext.Enter(
            normalized,
            allowLegacyDefault: normalized == MemoryTenantContext.LegacyDefaultUsername);
        return await operation();
    }
}
