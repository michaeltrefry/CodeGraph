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

/// <summary>Per-request decoded UTF-16 text limits, applied before sidecar parsing.</summary>
public sealed record TypeScriptSourceLimits
{
    // Independent of decoded text: empty files and path metadata still consume memory.
    public const int MaxAdmittedPaths = 50_000;
    public const int MaxPathCharacters = 8 * 1024 * 1024;
    public const int MaxReadFiles = 20_000;

    public static void ValidateScope(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (paths.Count > MaxAdmittedPaths)
            throw new InvalidOperationException("Frontend admitted path count exceeds its bounded analysis limit.");
        long characters = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            characters += path.Length;
            if (characters > MaxPathCharacters)
                throw new InvalidOperationException("Frontend admitted path text exceeds its bounded analysis limit.");
        }
    }

    public int MaxFileSizeKb { get; init; } = 512;
    public IReadOnlyDictionary<string, int> MaxFileSizeKbByExtension { get; init; } = new Dictionary<string, int>();
    public int MaxAnalysisTextKb { get; init; } = 64 * 1024;
    public int MaxFileSizeKbFor(string extension)
    {
        foreach (var (key, value) in MaxFileSizeKbByExtension)
            if (string.Equals(key, extension, StringComparison.OrdinalIgnoreCase)) return value;
        return MaxFileSizeKb;
    }
}
