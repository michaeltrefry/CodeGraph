namespace CodeGraph.Models;

public record ExtractionResult
{
    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];
    public IReadOnlyList<PendingEdge> Edges { get; init; } = [];
    public IReadOnlyList<UnresolvedCall> UnresolvedCalls { get; init; } = [];
    public IReadOnlyList<UnresolvedImport> UnresolvedImports { get; init; } = [];

    /// <summary>
    /// Optional metadata about the project detected by the extractor.
    /// Only set by project-level analyzers (SolutionAnalyzer, TypeScriptProjectAnalyzer).
    /// </summary>
    public ProjectMetadata? Metadata { get; init; }
}

/// <summary>
/// Language/framework metadata detected by an extractor.
/// </summary>
public record ProjectMetadata(
    string Language,
    string? Framework,
    DotnetSupportInfo? DotnetSupport = null);

/// <summary>
/// Edge where target is a qualified name (not yet resolved to a node ID).
/// </summary>
public record PendingEdge(
    string SourceQN,
    string TargetQN,
    EdgeType Type,
    Dictionary<string, object>? Properties = null);

/// <summary>
/// Call site that needs cross-reference resolution.
/// </summary>
public record UnresolvedCall(
    string CallerQN,
    string CalleeName,
    string? ReceiverType,
    double Confidence);

/// <summary>
/// Import/using that needs module resolution.
/// </summary>
public record UnresolvedImport(
    string FileQN,
    string ImportedNamespace);

public enum ConfidenceLevel
{
    High,
    Medium,
    Low
}
