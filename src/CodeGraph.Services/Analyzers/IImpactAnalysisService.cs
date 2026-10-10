using CodeGraph.Data;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Analyzers;

public interface IImpactAnalysisService
{
    /// <summary>
    /// Analyze blast radius for a node identified by qualified name. <paramref name="graph"/>
    /// selects the graph to answer from (for example a branch overlay); the default-branch store when null.
    /// </summary>
    Task<ImpactReport?> AnalyzeImpactAsync(string qualifiedName, string? project = null, int maxDepth = 3, IGraphReader? graph = null);

    /// <summary>
    /// Analyze blast radius for all nodes in a file (union of their individual blast radii).
    /// </summary>
    Task<ImpactReport?> AnalyzeFileImpactAsync(string project, string filePath, int maxDepth = 3, IGraphReader? graph = null);
}
