using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Registration-only extraction after callable definitions are available. The caller supplies only
/// current and eligible persisted nodes from the admitted repository scope, including SCIP nodes.
/// Successful empty output replaces removed registrations; failure must retain the previous slice.
/// </summary>
public interface IBackendEndpointExtractor
{
    bool SupportsBackendEndpoints(string extension);

    /// <summary>Conservative admission gate. Previously endpoint-owned files must also run,
    /// even when this returns false, so removed registrations can replace their old facts.</summary>
    bool MayContainBackendEndpoints(string extension, string content);

    Task<ExtractionResult> ExtractBackendEndpointsAsync(string filePath, string content,
        ExtractorContext context, IReadOnlyCollection<GraphNode> availableNodes,
        CancellationToken ct = default);
}
