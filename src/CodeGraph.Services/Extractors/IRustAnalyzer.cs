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
public sealed record RustWorkspaceAnalysis(IReadOnlyList<RustCrateGroupAnalysis> Groups)
{
    /// <summary>The crate-group settings the analysis ran with, when the analyzer reports them.</summary>
    public RustSemanticSettingsSnapshot? Settings { get; init; }
}

/// <summary>Effective crate-group settings of one Rust semantic analysis.</summary>
public sealed record RustSemanticSettingsSnapshot(
    int GroupSize,
    int GroupTimeoutSeconds,
    int WorkspaceTimeoutSeconds,
    int GroupMemoryLimitMb,
    bool BuildScripts,
    bool ProcMacros);

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

    /// <summary>Zero-based position of the group in the workspace's deterministic group order.</summary>
    public int Index { get; init; }

    /// <summary>True when the group never started because the workspace deadline had passed.</summary>
    public bool Skipped { get; init; }

    /// <summary>Wall-clock time spent analyzing the group; null when the analyzer did not measure it.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Highest resident memory sampled for any of the group's commands (summed over the command's
    /// process group from /proc); null where the platform offers no such sampling.
    /// </summary>
    public long? PeakRssBytes { get; init; }

    public static RustCrateGroupAnalysis WholeWorkspace(IReadOnlyList<ExtractionResult> results) =>
        new([], [], results, null);
}
