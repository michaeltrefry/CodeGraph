using CodeGraph.Models.Memory;

namespace CodeGraph.Services.Memory;

public interface IMemoryAdministrationService
{
    Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        string targetUsername,
        int staleAfterMinutes,
        int sampleLimit,
        CancellationToken ct = default);

    Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
        string targetUsername,
        int staleAfterMinutes,
        int sampleLimit,
        CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(
        string targetUsername,
        string source,
        bool dryRun,
        CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(
        string targetUsername,
        bool dryRun,
        CancellationToken ct = default);

    Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
        string targetUsername,
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default);
}
