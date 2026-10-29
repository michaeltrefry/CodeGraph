using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Analyzes a TypeScript/Angular repository for semantic code extraction.
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

    /// <summary>
    /// Analyzes the project of <paramref name="tsconfigPath"/>, reading only
    /// <paramref name="includedFiles"/>: the repo-relative TypeScript sources, tsconfig.json and
    /// package.json files the pipeline's discovery admits. Nothing outside them (agent worktrees,
    /// dependency directories, other skip patterns) may be analyzed or discovered as a workspace
    /// package, whatever tsconfig <c>include</c>/<c>references</c> say.
    /// </summary>
    Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string tsconfigPath,
        ExtractorContext context,
        IReadOnlyList<string> includedFiles,
        CancellationToken ct = default);
}
