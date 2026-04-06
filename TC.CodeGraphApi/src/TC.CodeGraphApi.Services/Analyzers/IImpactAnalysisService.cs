using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services.Analyzers;

public interface IImpactAnalysisService
{
    /// <summary>
    /// Analyze blast radius for a node identified by qualified name.
    /// </summary>
    Task<ImpactReport?> AnalyzeImpactAsync(string qualifiedName, string? project = null, int maxDepth = 3);

    /// <summary>
    /// Analyze blast radius for all nodes in a file (union of their individual blast radii).
    /// </summary>
    Task<ImpactReport?> AnalyzeFileImpactAsync(string project, string filePath, int maxDepth = 3);
}
