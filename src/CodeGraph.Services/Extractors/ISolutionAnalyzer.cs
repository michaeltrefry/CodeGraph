using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Analyzes a full solution file for semantic code extraction.
/// Implemented by language-specific extractors (e.g., Roslyn for C#).
/// </summary>
public interface ISolutionAnalyzer
{
    /// <summary>
    /// Version of this analyzer's output; part of the per-file extraction version of the files
    /// it covers. Bump it whenever the output changes for unchanged input — see
    /// <see cref="CodeGraph.Services.Pipeline.ExtractionVersionCatalog"/>.
    /// </summary>
    string ExtractionVersion => "1";

    Task<SolutionAnalysisResult> AnalyzeSolutionAsync(
        string solutionPath, ExtractorContext context, CancellationToken ct);
}

/// <summary>
/// The successful document-level output of one solution analysis. Keeping the
/// source path beside its extraction result lets the pipeline deduplicate shared
/// projects across solutions and fall back only for documents that Roslyn did
/// not successfully analyze.
/// </summary>
public sealed record SolutionDocumentAnalysis(string FilePath, ExtractionResult Result);

public sealed record SolutionAnalysisResult
{
    public IReadOnlyList<SolutionDocumentAnalysis> Documents { get; init; } = [];
    public ProjectMetadata? Metadata { get; init; }
}
