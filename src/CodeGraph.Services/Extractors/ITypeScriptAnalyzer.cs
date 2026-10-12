using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Analyzes a TypeScript/Angular project for semantic code extraction.
/// Implemented by TypeScriptProjectAnalyzer, which calls the Node.js sidecar.
/// </summary>
public interface ITypeScriptAnalyzer
{
    /// <summary>
    /// Version of this analyzer's output; part of the per-file extraction version of the files
    /// it covers. Bump it whenever the output changes for unchanged input — see
    /// <see cref="CodeGraph.Services.Pipeline.ExtractionVersionCatalog"/>.
    /// </summary>
    string ExtractionVersion => "1";

    Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string tsconfigPath, ExtractorContext context, CancellationToken ct = default);
}
