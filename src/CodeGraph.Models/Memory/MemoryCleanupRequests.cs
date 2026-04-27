namespace CodeGraph.Models.Memory;

public sealed class MemoryCleanupBySourceRequest
{
    public string Source { get; init; } = string.Empty;
    public bool DryRun { get; init; }
}

public sealed class MemoryCleanupTestDataRequest
{
    public bool DryRun { get; init; }
}

public sealed class MemoryCleanupByIdsRequest
{
    public IReadOnlyList<string> ClaimIds { get; init; } = [];
    public IReadOnlyList<string> EntityIds { get; init; } = [];
    public bool DryRun { get; init; }
}
