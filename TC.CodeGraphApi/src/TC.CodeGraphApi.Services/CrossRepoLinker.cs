using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services;

public class CrossRepoLinker
{
    private readonly IGraphStore _store;
    private readonly ILogger<CrossRepoLinker> _logger;

    public CrossRepoLinker(IGraphStore store, ILogger<CrossRepoLinker> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task LinkAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting cross-repo linking");

        var httpLinks = await LinkHttpRoutesAsync(ct);
        var messagingLinks = await LinkMessagingAsync(ct);
        var nugetLinks = await LinkNuGetPackagesAsync(ct);

        _logger.LogInformation(
            "Cross-repo linking complete: {Http} HTTP, {Messaging} messaging, {NuGet} NuGet links",
            httpLinks, messagingLinks, nugetLinks);
    }

    private async Task<int> LinkHttpRoutesAsync(CancellationToken ct)
    {
        // Collect all Route nodes across all projects
        var routes = await _store.FindAllNodesByLabelAsync(NodeLabel.Route);
        if (routes.Count == 0)
            return 0;

        // Collect all HTTP_CALLS edges
        var httpCallEdges = await _store.FindAllEdgesByTypeAsync(EdgeType.HTTP_CALLS);
        if (httpCallEdges.Count == 0)
            return 0;

        // Build route lookup: normalize templates for matching
        var routeLookup = routes
            .GroupBy(r => NormalizeRouteTemplate(
                r.Properties.GetValueOrDefault("http_method")?.ToString() ?? "",
                r.Properties.GetValueOrDefault("route_template")?.ToString() ?? ""))
            .ToDictionary(g => g.Key, g => g.ToList());

        var crossEdges = new List<CrossRepoEdge>();

        foreach (var edge in httpCallEdges)
        {
            // The edge target QN encodes the route: route:*:METHOD:url_pattern
            var urlPattern = edge.Properties.GetValueOrDefault("url_pattern")?.ToString();
            var httpMethod = edge.Properties.GetValueOrDefault("http_method")?.ToString();

            if (urlPattern is null || httpMethod is null)
                continue;

            var normalizedCall = NormalizeUrlForMatching(httpMethod, urlPattern);

            // Try exact match first, then pattern match
            var matchedRoutes = FindMatchingRoutes(normalizedCall, httpMethod, urlPattern, routeLookup);

            foreach (var route in matchedRoutes)
            {
                // Only create cross-repo edges when source and target are in different projects
                var sourceNode = await FindNodeByEdgeSourceAsync(edge);
                if (sourceNode is null || sourceNode.Project == route.Project)
                    continue;

                crossEdges.Add(new CrossRepoEdge
                {
                    SourceProject = sourceNode.Project,
                    TargetProject = route.Project,
                    SourceNodeId = edge.SourceId,
                    TargetNodeId = route.Id,
                    Type = EdgeType.HTTP_CALLS,
                    Properties = new()
                    {
                        ["http_method"] = httpMethod,
                        ["url_pattern"] = urlPattern,
                        ["route_template"] = route.Properties.GetValueOrDefault("route_template") ?? "",
                        ["confidence_band"] = "medium"
                    }
                });
            }
        }

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges);
            _logger.LogInformation("Linked {Count} HTTP call(s) to routes across repos", crossEdges.Count);
        }

        return crossEdges.Count;
    }

    private async Task<int> LinkMessagingAsync(CancellationToken ct)
    {
        var publishEdges = await _store.FindAllEdgesByTypeAsync(EdgeType.PUBLISHES);
        var consumeEdges = await _store.FindAllEdgesByTypeAsync(EdgeType.CONSUMES);

        if (publishEdges.Count == 0 || consumeEdges.Count == 0)
            return 0;

        // Group consumers by the event type QN they consume
        // Target node QN is the event type
        var consumersByEvent = new Dictionary<string, List<GraphEdge>>();
        foreach (var edge in consumeEdges)
        {
            var targetNode = await FindNodeByIdAsync(edge.TargetId);
            if (targetNode is null) continue;

            if (!consumersByEvent.TryGetValue(targetNode.QualifiedName, out var list))
            {
                list = new List<GraphEdge>();
                consumersByEvent[targetNode.QualifiedName] = list;
            }
            list.Add(edge);
        }

        var crossEdges = new List<CrossRepoEdge>();

        foreach (var pubEdge in publishEdges)
        {
            var pubTarget = await FindNodeByIdAsync(pubEdge.TargetId);
            if (pubTarget is null) continue;

            if (!consumersByEvent.TryGetValue(pubTarget.QualifiedName, out var consumers))
                continue;

            var publisherSource = await FindNodeByEdgeSourceAsync(pubEdge);
            if (publisherSource is null) continue;

            foreach (var conEdge in consumers)
            {
                var consumerSource = await FindNodeByEdgeSourceAsync(conEdge);
                if (consumerSource is null || consumerSource.Project == publisherSource.Project)
                    continue;

                crossEdges.Add(new CrossRepoEdge
                {
                    SourceProject = publisherSource.Project,
                    TargetProject = consumerSource.Project,
                    SourceNodeId = pubEdge.SourceId,
                    TargetNodeId = conEdge.SourceId,
                    Type = EdgeType.PUBLISHES,
                    Properties = new()
                    {
                        ["event_type"] = pubTarget.QualifiedName,
                        ["confidence_band"] = "high"
                    }
                });
            }
        }

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges);
            _logger.LogInformation("Linked {Count} publisher→consumer pair(s) across repos", crossEdges.Count);
        }

        return crossEdges.Count;
    }

    private async Task<int> LinkNuGetPackagesAsync(CancellationToken ct)
    {
        // Find all NuGetPackage nodes
        var nugetNodes = await _store.FindAllNodesByLabelAsync(NodeLabel.NuGetPackage);
        if (nugetNodes.Count == 0)
            return 0;

        // Find all REFERENCES_PACKAGE edges
        var refEdges = await _store.FindAllEdgesByTypeAsync(EdgeType.REFERENCES_PACKAGE);
        if (refEdges.Count == 0)
            return 0;

        // Get all known projects
        var projects = await _store.ListProjectsAsync();
        var projectNames = projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Map TC.* package names to project names
        // Convention: TC.OrdersApi.Models package → TC.OrdersApi project
        var packageToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in nugetNodes.Where(n => n.Name.StartsWith("TC.", StringComparison.OrdinalIgnoreCase)))
        {
            var candidateProject = DerivProjectFromPackage(pkg.Name);
            if (candidateProject is not null && projectNames.Contains(candidateProject))
            {
                packageToProject[pkg.Name] = candidateProject;
            }
        }

        var crossEdges = new List<CrossRepoEdge>();

        foreach (var edge in refEdges)
        {
            var nugetNode = await FindNodeByIdAsync(edge.TargetId);
            if (nugetNode is null) continue;

            if (!packageToProject.TryGetValue(nugetNode.Name, out var targetProject))
                continue;

            var sourceNode = await FindNodeByEdgeSourceAsync(edge);
            if (sourceNode is null || sourceNode.Project == targetProject)
                continue;

            crossEdges.Add(new CrossRepoEdge
            {
                SourceProject = sourceNode.Project,
                TargetProject = targetProject,
                SourceNodeId = edge.SourceId,
                TargetNodeId = nugetNode.Id,
                Type = EdgeType.REFERENCES_PACKAGE,
                Properties = new()
                {
                    ["package_name"] = nugetNode.Name,
                    ["version"] = nugetNode.Properties.GetValueOrDefault("version") ?? "",
                    ["confidence_band"] = "high"
                }
            });
        }

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges);
            _logger.LogInformation("Linked {Count} NuGet package reference(s) across repos", crossEdges.Count);
        }

        return crossEdges.Count;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<GraphNode?> FindNodeByEdgeSourceAsync(GraphEdge edge)
    {
        // Look up the source node to determine its project
        var nodes = await _store.FindEdgesBySourceAsync(edge.SourceId);
        // We need the actual node — use search by traversal from source
        var results = await _store.TraverseAsync(edge.SourceId, TraceDirection.Outbound, 0);
        return results.FirstOrDefault()?.Node;
    }

    private async Task<GraphNode?> FindNodeByIdAsync(long nodeId)
    {
        var results = await _store.TraverseAsync(nodeId, TraceDirection.Outbound, 0);
        return results.FirstOrDefault()?.Node;
    }

    /// <summary>
    /// Normalize a route template for matching: lowercase, strip leading slash,
    /// replace parameter placeholders with a wildcard token.
    /// </summary>
    private static string NormalizeRouteTemplate(string httpMethod, string template)
    {
        var normalized = template.ToLowerInvariant().TrimStart('/');
        // Replace {anything} with {*} for matching
        normalized = Regex.Replace(normalized, @"\{[^}]+\}", "{*}");
        return $"{httpMethod.ToUpperInvariant()}:{normalized}";
    }

    /// <summary>
    /// Normalize a concrete URL for matching against route templates:
    /// replace path segments that look like IDs with {*}.
    /// </summary>
    private static string NormalizeUrlForMatching(string httpMethod, string url)
    {
        var normalized = url.ToLowerInvariant().TrimStart('/');
        // Replace segments that are numeric or GUIDs with {*}
        var segments = normalized.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (IsLikelyId(segments[i]))
                segments[i] = "{*}";
        }
        return $"{httpMethod.ToUpperInvariant()}:{string.Join("/", segments)}";
    }

    private static bool IsLikelyId(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return false;
        // Numeric
        if (long.TryParse(segment, out _))
            return true;
        // GUID
        if (Guid.TryParse(segment, out _))
            return true;
        // Already a parameter placeholder
        if (segment.StartsWith('{') && segment.EndsWith('}'))
            return true;
        return false;
    }

    private static List<GraphNode> FindMatchingRoutes(
        string normalizedCall, string httpMethod, string urlPattern,
        Dictionary<string, List<GraphNode>> routeLookup)
    {
        // Exact match after normalization
        if (routeLookup.TryGetValue(normalizedCall, out var exact))
            return exact;

        // Fuzzy: try normalizing the URL as a template too
        var asTemplate = NormalizeRouteTemplate(httpMethod, urlPattern);
        if (routeLookup.TryGetValue(asTemplate, out var templateMatch))
            return templateMatch;

        return [];
    }

    /// <summary>
    /// Derive project name from a NuGet package name.
    /// TC.OrdersApi.Models → TC.OrdersApi
    /// TC.Common.ServiceStack → TC.Common.ServiceStack (if it's a project)
    /// </summary>
    private static string? DerivProjectFromPackage(string packageName)
    {
        // Try removing common suffixes: .Models, .Contracts, .Client
        var suffixes = new[] { ".Models", ".Contracts", ".Client", ".Shared" };
        foreach (var suffix in suffixes)
        {
            if (packageName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return packageName[..^suffix.Length];
        }

        // Maybe the package name IS the project name
        return packageName;
    }
}
