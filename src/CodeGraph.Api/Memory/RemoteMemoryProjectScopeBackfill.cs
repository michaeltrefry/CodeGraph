using CodeGraph.Data;
using CodeGraph.Memory.Client;

namespace CodeGraph.Api.Memory;

/// <summary>
/// The project-scope backfill (CG-167) when memory lives on the memory host: run there, against the
/// memory host's database, as the system identity.
/// </summary>
public sealed class RemoteMemoryProjectScopeBackfill(IMemoryClient memoryClient) : IMemoryProjectScopeBackfill
{
    public Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
        => memoryClient.BackfillProjectScopesAsync(MemoryTenantContext.SystemUsername, projectKey, repositories, ct);
}
