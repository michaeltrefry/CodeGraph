using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Analyzes a Cargo project or workspace for semantic Rust code extraction.
/// </summary>
public interface IRustAnalyzer
{
    /// <summary>
    /// Analyzes the whole workspace and returns its semantic results, or throws when any
    /// part of it could not be analyzed.
    /// </summary>
    Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string cargoManifestPath, ExtractorContext context, CancellationToken ct = default);

    /// <summary>
    /// Analyzes the workspace one crate group at a time. A failed group is reported in its
    /// <see cref="RustCrateGroupAnalysis.Failure"/> instead of failing the other groups; a
    /// thrown exception means no group could be analyzed (for example missing tools).
    /// The default treats the whole workspace as a single group.
    /// </summary>
    async Task<RustWorkspaceAnalysis> AnalyzeWorkspaceAsync(
        string cargoManifestPath, ExtractorContext context, CancellationToken ct = default)
    {
        var results = await AnalyzeProjectAsync(cargoManifestPath, context, ct);
        return new RustWorkspaceAnalysis([RustCrateGroupAnalysis.WholeWorkspace(results)]);
    }
}

/// <summary>Per-crate-group outcome of one Cargo workspace analysis.</summary>
public sealed record RustWorkspaceAnalysis(IReadOnlyList<RustCrateGroupAnalysis> Groups);

/// <summary>
/// One crate group's semantic analysis.
/// </summary>
/// <param name="Crates">Cargo package names in the group; empty for a whole-workspace group.</param>
/// <param name="SourceDirectories">
/// Repository-relative package directories ("/"-separated, "" for the repository root) of the
/// group's crates; empty for a whole-workspace group.
/// </param>
/// <param name="Results">Semantic results when the group succeeded.</param>
/// <param name="Failure">Why the group failed; null when it succeeded.</param>
public sealed record RustCrateGroupAnalysis(
    IReadOnlyList<string> Crates,
    IReadOnlyList<string> SourceDirectories,
    IReadOnlyList<ExtractionResult>? Results,
    Exception? Failure)
{
    public bool IsWholeWorkspace => Crates.Count == 0 && SourceDirectories.Count == 0;

    public static RustCrateGroupAnalysis WholeWorkspace(IReadOnlyList<ExtractionResult> results) =>
        new([], [], results, null);
}
