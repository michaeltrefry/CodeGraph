using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Analyzes a TypeScript/Angular project for semantic code extraction.
/// Implemented by TypeScriptProjectAnalyzer, which calls the Node.js sidecar.
/// </summary>
public interface ITypeScriptAnalyzer
{
    Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string tsconfigPath, ExtractorContext context, CancellationToken ct = default);
}
