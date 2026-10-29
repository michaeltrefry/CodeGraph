using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Analyzers;

/// <summary>
/// Merges lint results from multiple sources:
/// 1. LintResultCache (Roslyn diagnostics stashed during indexing)
/// 2. ILintRunner delegate (ESLint via ts-extractor sidecar)
/// </summary>
public class CompositeLintRunner(
    LintResultCache cache,
    ILintRunner sidecarRunner,
    ILogger<CompositeLintRunner> logger) : ILintRunner
{
    public async Task<IReadOnlyDictionary<string, LintResult>> LintRepositoryAsync(
        string repoPath, CancellationToken ct = default)
    {
        // Extract the repository name from the repo path (last segment)
        var repositoryName = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var merged = new Dictionary<string, LintResult>(StringComparer.OrdinalIgnoreCase);

        // 1. Check cache for Roslyn diagnostics (C# files)
        if (cache.HasResults(repositoryName))
        {
            var roslynResults = cache.Take(repositoryName);
            foreach (var (file, result) in roslynResults)
                merged[file] = result;

            logger.LogInformation("Loaded {Count} Roslyn lint results from cache for {Project}",
                roslynResults.Count, repositoryName);
        }

        // 2. Call sidecar for ESLint results (TS/JS files)
        var eslintResults = await sidecarRunner.LintRepositoryAsync(repoPath, ct);
        foreach (var (file, result) in eslintResults)
        {
            // ESLint results won't overlap with Roslyn (different file extensions)
            merged[file] = result;
        }

        return merged;
    }
}
