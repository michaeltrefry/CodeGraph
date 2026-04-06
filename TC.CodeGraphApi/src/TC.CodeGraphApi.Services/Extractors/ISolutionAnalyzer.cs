using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services.Extractors;

/// <summary>
/// Analyzes a full solution file for semantic code extraction.
/// Implemented by language-specific extractors (e.g., Roslyn for C#).
/// </summary>
public interface ISolutionAnalyzer
{
    Task<IReadOnlyList<ExtractionResult>> AnalyzeSolutionAsync(
        string solutionPath, ExtractorContext context, CancellationToken ct);
}
