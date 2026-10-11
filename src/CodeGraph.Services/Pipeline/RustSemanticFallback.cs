using System.Collections.Concurrent;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// A Cargo workspace or crate group whose rust-analyzer SCIP pass failed during an index run
/// and whose <c>.rs</c> files were indexed by the tree-sitter Rust extractor instead. A run
/// records one fallback per failed crate group (or one for the whole workspace when no group
/// could be analyzed), in the workspace's deterministic group order.
/// </summary>
/// <param name="Manifest">Repository-relative path of the Cargo manifest that was analyzed.</param>
/// <param name="Crates">
/// Cargo package names affected by the failure: exactly the failed group's crates, or every
/// crate of the workspace for a whole-workspace failure.
/// </param>
/// <param name="FailureCode">One of the <see cref="Extractors.RustSemanticFailureCategory"/> codes.</param>
/// <param name="DetailCode">The analyzer's specific failure code, when it provided one.</param>
/// <param name="Message">The analyzer's failure message.</param>
public sealed record RustSemanticFallback(
    string Manifest,
    IReadOnlyList<string> Crates,
    string FailureCode,
    string? DetailCode,
    string Message)
{
    /// <summary>
    /// Repository-relative directories ("/"-separated, "" for the repository root) whose
    /// <c>.rs</c> files the fallback covers, unless a nested crate directory owns the file.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> SourceDirectories { get; init; } = [];
}

/// <summary>
/// Scoped collector for non-fatal indexing degradations, so the operation hosting an
/// index run (for example a durable indexer run) can record them with its outcome.
/// </summary>
public sealed class IndexingRunDiagnostics
{
    private readonly ConcurrentQueue<RustSemanticFallback> _rustSemanticFallbacks = new();

    public IReadOnlyList<RustSemanticFallback> RustSemanticFallbacks => _rustSemanticFallbacks.ToArray();

    public void RecordRustSemanticFallback(RustSemanticFallback fallback) =>
        _rustSemanticFallbacks.Enqueue(fallback);

    /// <summary>Per-group outcomes of the run's Rust semantic analysis; null when none ran.</summary>
    public RustSemanticOutcomes? RustSemanticOutcomes { get; private set; }

    public void RecordRustSemanticOutcomes(RustSemanticOutcomes outcomes) =>
        RustSemanticOutcomes = outcomes;
}
