using CodeGraph.Data;
using CodeGraph.Memory.Client;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;

namespace CodeGraph.Api.Memory;

public sealed class RemoteMemoryAdministrationService(IMemoryClient memoryClient) : IMemoryAdministrationService
{
    public Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        string targetUsername,
        int staleAfterMinutes,
        int sampleLimit,
        CancellationToken ct = default)
        => memoryClient.GetWriteDiagnosticsAsync(Target(targetUsername), staleAfterMinutes, sampleLimit, ct);

    public Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
        string targetUsername,
        int staleAfterMinutes,
        int sampleLimit,
        CancellationToken ct = default)
        => memoryClient.GetDiagnosticsAsync(Target(targetUsername), staleAfterMinutes, sampleLimit, ct);

    public Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(
        string targetUsername,
        string source,
        bool dryRun,
        CancellationToken ct = default)
        => memoryClient.DeleteBySourceAsync(Target(targetUsername), source, dryRun, ct);

    public Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(
        string targetUsername,
        bool dryRun,
        CancellationToken ct = default)
        => memoryClient.DeleteTestDataAsync(Target(targetUsername), dryRun, ct);

    public Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
        string targetUsername,
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default)
        => memoryClient.DeleteByIdsAsync(Target(targetUsername), claimIds, entityIds, dryRun, ct);

    private static string Target(string username) => MemoryTenantContext.NormalizeAdministrativeTarget(username);
}
