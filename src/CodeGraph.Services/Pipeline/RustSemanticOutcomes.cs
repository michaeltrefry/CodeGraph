using System.Globalization;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Outcome of every crate group of one Rust semantic (SCIP) analysis, recorded for each index
/// run that analyzed a Cargo workspace (in repositories.properties.rustSemanticOutcomes and,
/// for durable runs, summarized in the run message).
/// </summary>
/// <param name="Manifest">Repository-relative path of the analyzed Cargo manifest.</param>
/// <param name="CrateCount">Crates covered by all groups.</param>
/// <param name="GroupCount">Number of groups (one for a whole-workspace analysis or failure).</param>
/// <param name="GroupSize">Configured maximum crates per group.</param>
/// <param name="SemanticGroups">Groups whose semantic results were merged into the graph.</param>
/// <param name="FallbackGroups">Groups that ran but fell back to tree-sitter extraction.</param>
/// <param name="SkippedGroups">Groups never started because the workspace timeout had passed.</param>
/// <param name="TotalDurationMs">Wall-clock time of the whole workspace analysis.</param>
/// <param name="Settings">Effective crate-group settings.</param>
/// <param name="Groups">One entry per group, in deterministic group order.</param>
public sealed record RustSemanticOutcomes(
    string Manifest,
    int CrateCount,
    int GroupCount,
    int GroupSize,
    int SemanticGroups,
    int FallbackGroups,
    int SkippedGroups,
    long TotalDurationMs,
    RustSemanticSettingsSnapshot Settings,
    IReadOnlyList<RustSemanticGroupOutcome> Groups)
{
    /// <summary>One-line human summary, e.g. for an indexer run message.</summary>
    public string Summary() => string.Create(
        CultureInfo.InvariantCulture,
        $"Rust semantic: {GroupCount} groups ({SemanticGroups} semantic, {FallbackGroups} fallback, " +
        $"{SkippedGroups} skipped), {CrateCount} crates, {Math.Round(TotalDurationMs / 1000.0)}s.");
}

/// <summary>Outcome of one crate group.</summary>
/// <param name="Index">Zero-based group position in the workspace's deterministic group order.</param>
/// <param name="Crates">Cargo package names of the group (every workspace crate for a whole-workspace group).</param>
/// <param name="Outcome"><see cref="RustSemanticGroupOutcome.Semantic"/>, <see cref="RustSemanticGroupOutcome.Fallback"/> or <see cref="RustSemanticGroupOutcome.Skipped"/>.</param>
/// <param name="FailureCode">The <see cref="RustSemanticFailureCategory"/> code when not semantic.</param>
/// <param name="DetailCode">The analyzer's specific failure code when not semantic.</param>
/// <param name="DurationMs">Wall-clock time spent on the group (0 when skipped).</param>
/// <param name="PeakRssMb">Peak sampled process-group RSS (Linux /proc sampling); null elsewhere.</param>
/// <param name="Documents">Source documents imported (semantic groups only).</param>
/// <param name="Nodes">Graph nodes imported before cross-group linking (semantic groups only).</param>
/// <param name="Edges">Graph edges imported before cross-group linking (semantic groups only).</param>
public sealed record RustSemanticGroupOutcome(
    int Index,
    IReadOnlyList<string> Crates,
    string Outcome,
    string? FailureCode,
    string? DetailCode,
    long DurationMs,
    long? PeakRssMb,
    int? Documents,
    int? Nodes,
    int? Edges)
{
    public const string Semantic = "semantic";
    public const string Fallback = "fallback";
    public const string Skipped = "skipped";
}
