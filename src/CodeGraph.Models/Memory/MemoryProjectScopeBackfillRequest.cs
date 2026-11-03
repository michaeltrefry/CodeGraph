namespace CodeGraph.Models.Memory;

/// <summary>A project and its member repositories, whose existing memory the memory host scopes to it (CG-167).</summary>
public sealed class MemoryProjectScopeBackfillRequest
{
    public string ProjectKey { get; init; } = string.Empty;
    public IReadOnlyList<string> Repositories { get; init; } = [];
}
