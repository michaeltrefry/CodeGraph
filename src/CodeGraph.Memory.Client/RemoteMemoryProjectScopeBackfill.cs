using CodeGraph.Data;
using CodeGraph.Models.Memory;

namespace CodeGraph.Memory.Client;

/// <summary>
/// The project-scope backfill (CG-167) when memory lives on the memory host: run there, against the
/// memory host's database, as the system identity. Used by the API (a project's create or repository
/// change) and the Jobs host (the daily reconciliation, with its watermark and range backfill, CG-172).
/// </summary>
public sealed class RemoteMemoryProjectScopeBackfill(IMemoryClient memoryClient) : IMemoryProjectScopeBackfill
{
    public Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
        => memoryClient.BackfillProjectScopesAsync(MemoryTenantContext.SystemUsername, projectKey, repositories, ct);

    public Task<MemoryScopeWatermark> GetWatermarkAsync(MemoryScopeWatermark? since, CancellationToken ct = default)
        => memoryClient.GetProjectScopeWatermarkAsync(MemoryTenantContext.SystemUsername, since, ct);

    public Task BackfillRangeAsync(IReadOnlyList<ProjectRepositories> projects, MemoryScopeWatermark after, MemoryScopeWatermark upTo, CancellationToken ct = default)
        => memoryClient.BackfillProjectScopeRangeAsync(
            MemoryTenantContext.SystemUsername,
            projects.Select(project => new MemoryProjectScopeBackfillRequest { ProjectKey = project.ProjectKey, Repositories = project.Repositories }).ToList(),
            after,
            upTo,
            ct);
}
