using System.Collections.Concurrent;
using CodeGraph.Data;

namespace CodeGraph.Services.Analyzers;

/// <summary>
/// In-memory cache for detailed diagnostics collected during code extraction.
/// VitalsAnalyzer consumes these details during metrics computation and persists
/// them to the graph alongside aggregate lint counts.
/// </summary>
public class DiagnosticDetailCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<ProjectDiagnosticEntity>> _store = new(
        StringComparer.OrdinalIgnoreCase);

    public void Set(string projectName, IReadOnlyList<ProjectDiagnosticEntity> diagnostics)
    {
        _store[projectName] = diagnostics.ToList();
    }

    public IReadOnlyList<ProjectDiagnosticEntity> Take(string projectName)
    {
        return _store.TryRemove(projectName, out var diagnostics)
            ? diagnostics
            : [];
    }

    public bool HasResults(string projectName) => _store.ContainsKey(projectName);
}
