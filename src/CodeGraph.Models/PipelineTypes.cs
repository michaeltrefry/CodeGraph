namespace CodeGraph.Models;

public record ExtractionResult
{
    /// <summary>
    /// Whether extraction completed successfully. Successful results may legitimately
    /// contain no nodes; failed results must not replace an existing graph slice or
    /// advance its file hash.
    /// </summary>
    public bool Succeeded { get; init; } = true;

    /// <summary>
    /// Human-readable failure context for diagnostics. This is intentionally not used
    /// to infer success: callers must inspect <see cref="Succeeded"/>.
    /// </summary>
    public string? FailureReason { get; init; }

    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];
    public IReadOnlyList<PendingEdge> Edges { get; init; } = [];
    public IReadOnlyList<UnresolvedCall> UnresolvedCalls { get; init; } = [];
    public IReadOnlyList<UnresolvedImport> UnresolvedImports { get; init; } = [];

    /// <summary>
    /// Repository-relative files that a project-level analyzer completed successfully.
    /// Incremental indexing uses this to avoid advancing a hash for a partial result.
    /// </summary>
    public IReadOnlyList<string> ProcessedFiles { get; init; } = [];

    /// <summary>
    /// Optional metadata about the project detected by the extractor.
    /// Only set by project-level analyzers (SolutionAnalyzer, TypeScriptProjectAnalyzer).
    /// </summary>
    public ProjectMetadata? Metadata { get; init; }

    public static ExtractionResult Failure(string reason) => new()
    {
        Succeeded = false,
        FailureReason = reason
    };
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
