using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Analyzes a Cargo project or workspace for semantic Rust code extraction.
/// </summary>
public interface IRustAnalyzer
{
    Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string cargoManifestPath, ExtractorContext context, CancellationToken ct = default);
}
