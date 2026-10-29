using System.Collections.Concurrent;

namespace CodeGraph.Services.Analyzers;

/// <summary>
/// In-memory cache for lint diagnostics collected during code extraction.
/// Roslyn diagnostics are stashed here by SolutionAnalyzer; VitalsAnalyzer reads
/// them via ILintRunner during health scoring.
/// Scoped as singleton — persists across indexing → vitals pipeline stages.
/// </summary>
public class LintResultCache
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, LintResult>> _store = new();

    /// <summary>
    /// Store lint results for a repository. File paths should be relative to repo root.
    /// </summary>
    public void Set(string repositoryName, IReadOnlyDictionary<string, LintResult> results)
    {
        var dict = _store.GetOrAdd(repositoryName, _ => new ConcurrentDictionary<string, LintResult>(StringComparer.OrdinalIgnoreCase));
        foreach (var (file, result) in results)
            dict[file] = result;
    }

    /// <summary>
    /// Retrieve and clear lint results for a repository. Returns empty if none stashed.
    /// </summary>
    public IReadOnlyDictionary<string, LintResult> Take(string repositoryName)
    {
        if (_store.TryRemove(repositoryName, out var dict))
            return dict;
        return new Dictionary<string, LintResult>();
    }

    /// <summary>
    /// Check if there are any stashed results for a repository.
    /// </summary>
    public bool HasResults(string repositoryName) => _store.ContainsKey(repositoryName);
}
