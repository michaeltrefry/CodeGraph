using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services.Pipeline;

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

        // Run sequentially — the underlying DbContext is not thread-safe
        var http = await LinkHttpRoutesAsync(ct);
        var messaging = await LinkMessagingAsync(ct);
        var nuget = await LinkNuGetPackagesAsync(ct);
        var iac = await LinkIacDeploymentsAsync(ct);

        _logger.LogInformation(
            "Cross-repo linking complete: {Http} HTTP, {Messaging} messaging, {NuGet} NuGet, {IaC} IaC links",
            http, messaging, nuget, iac);
    }

    /// <summary>
    /// Incremental cross-repo linking scoped to a single project.
    /// Removes stale cross-repo edges for this project, then re-links.
    /// Called by RepositoryIndexingCompletedConsumer after a repo is indexed.
    /// </summary>
    /// <summary>
    /// Incremental cross-repo linking scoped to a single project.
    /// Removes stale cross-repo edges for this project, then re-links.
    /// Runs sequentially (not parallel) because the underlying DbContext is not thread-safe
    /// when called from a consumer's scoped instance.
    /// </summary>
    public async Task LinkForProjectAsync(string project, CancellationToken ct)
    {
        _logger.LogInformation("Running incremental cross-repo linking for {Project}", project);

        // Clear existing cross-repo edges for this project so stale links are removed
        await _store.DeleteCrossRepoEdgesForProjectAsync(project);

        var http = await LinkHttpRoutesAsync(ct);
        var messaging = await LinkMessagingAsync(ct);
        var nuget = await LinkNuGetPackagesAsync(ct);
        var iac = await LinkIacDeploymentsAsync(ct);

        _logger.LogInformation(
            "Incremental linking for {Project} complete: {Http} HTTP, {Messaging} messaging, {NuGet} NuGet, {IaC} IaC",
            project, http, messaging, nuget, iac);
    }

    private async Task<int> LinkHttpRoutesAsync(CancellationToken ct)
    {
        // Collect all HTTP_CALLS edges
        var httpCallEdges = await _store.FindAllEdgesByTypeAsync(EdgeType.HTTP_CALLS);
        if (httpCallEdges.Count == 0)
            return 0;

        // Collect all Route nodes across all projects (may be empty for gateway-only calls)
        var routes = await _store.FindAllNodesByLabelAsync(NodeLabel.Route);

        // Build route lookup: normalize templates for matching
        var routeLookup = routes
            .GroupBy(r => NormalizeRouteTemplate(
                r.Properties.GetValueOrDefault("http_method")?.ToString() ?? "",
                r.Properties.GetValueOrDefault("route_template")?.ToString() ?? ""))
            .ToDictionary(g => g.Key, g => g.ToList());

        var crossEdges = new List<CrossRepoEdge>();

        // Collect all known projects for DTO→project resolution
        var projects = await _store.ListRepositoriesAsync();
        var projectNames = projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Batch-fetch all source nodes for HTTP_CALLS edges
        var sourceNodeIds = httpCallEdges.Select(e => e.SourceId).Distinct().ToList();
        var sourceNodeById = await _store.FindNodesByIdBatchAsync(sourceNodeIds);

        foreach (var edge in httpCallEdges)
        {
            var gatewayRaw = edge.Properties.GetValueOrDefault("gateway_call");
            var isGatewayCall = gatewayRaw is true || (gatewayRaw is string s && bool.TryParse(s, out var b) && b);
            var requestDto = edge.Properties.GetValueOrDefault("request_dto")?.ToString();

            if (isGatewayCall && requestDto is not null && !requestDto.StartsWith("route:"))
            {
                var serviceName = edge.Properties.GetValueOrDefault("service_name")?.ToString();
                var targetProject = ResolveGatewayTargetProject(serviceName, requestDto, projectNames);
                if (targetProject is null)
                    continue;

                if (!sourceNodeById.TryGetValue(edge.SourceId, out var sourceNode)) continue;
                if (sourceNode.Project == targetProject)
                    continue;

                crossEdges.Add(CreateCrossRepoEdge(
                    sourceNode.Project, targetProject, edge.SourceId, edge.TargetId,
                    EdgeType.HTTP_CALLS, new()
                    {
                        ["request_dto"] = requestDto,
                        ["gateway_call"] = true,
                        ["confidence_band"] = "medium"
                    }));
                continue;
            }

            // Standard route-based HTTP_CALLS matching
            var urlPattern = edge.Properties.GetValueOrDefault("url_pattern")?.ToString();
            var httpMethod = edge.Properties.GetValueOrDefault("http_method")?.ToString();

            if (urlPattern is null || httpMethod is null)
                continue;

            var normalizedCall = NormalizeUrlForMatching(httpMethod, urlPattern);

            // Try exact match first, then pattern match
            var matchedRoutes = FindMatchingRoutes(normalizedCall, httpMethod, urlPattern, routeLookup);

            foreach (var route in matchedRoutes)
            {
                if (!sourceNodeById.TryGetValue(edge.SourceId, out var sourceNode)) continue;
                if (sourceNode.Project == route.Project)
                    continue;

                crossEdges.Add(CreateCrossRepoEdge(
                    sourceNode.Project, route.Project, edge.SourceId, route.Id,
                    EdgeType.HTTP_CALLS, new()
                    {
                        ["http_method"] = httpMethod,
                        ["url_pattern"] = urlPattern,
                        ["route_template"] = route.Properties.GetValueOrDefault("route_template") ?? "",
                        ["confidence_band"] = "medium"
                    }));
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

        // Batch-fetch all node IDs referenced by these edges
        var allNodeIds = publishEdges.Select(e => e.TargetId)
            .Concat(publishEdges.Select(e => e.SourceId))
            .Concat(consumeEdges.Select(e => e.TargetId))
            .Concat(consumeEdges.Select(e => e.SourceId))
            .Distinct().ToList();
        var nodeById = await _store.FindNodesByIdBatchAsync(allNodeIds);

        // Group consumers by the event type QN they consume
        var consumersByEvent = new Dictionary<string, List<GraphEdge>>();
        foreach (var edge in consumeEdges)
        {
            if (!nodeById.TryGetValue(edge.TargetId, out var targetNode)) continue;

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
            if (!nodeById.TryGetValue(pubEdge.TargetId, out var pubTarget)) continue;

            if (!consumersByEvent.TryGetValue(pubTarget.QualifiedName, out var consumers))
                continue;

            if (!nodeById.TryGetValue(pubEdge.SourceId, out var publisherSource)) continue;

            foreach (var conEdge in consumers)
            {
                if (!nodeById.TryGetValue(conEdge.SourceId, out var consumerSource)) continue;
                if (consumerSource.Project == publisherSource.Project)
                    continue;

                crossEdges.Add(CreateCrossRepoEdge(
                    publisherSource.Project, consumerSource.Project, pubEdge.SourceId, conEdge.SourceId,
                    EdgeType.PUBLISHES, new()
                    {
                        ["event_type"] = pubTarget.QualifiedName,
                        ["confidence_band"] = "high"
                    }));
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
        var projects = await _store.ListRepositoriesAsync();
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

        // Batch-fetch all nodes referenced by these edges
        var allNuGetNodeIds = refEdges.Select(e => e.TargetId)
            .Concat(refEdges.Select(e => e.SourceId))
            .Distinct().ToList();
        var nugetNodeById = await _store.FindNodesByIdBatchAsync(allNuGetNodeIds);

        var crossEdges = new List<CrossRepoEdge>();

        foreach (var edge in refEdges)
        {
            if (!nugetNodeById.TryGetValue(edge.TargetId, out var nugetNode)) continue;

            if (!packageToProject.TryGetValue(nugetNode.Name, out var targetProject))
                continue;

            if (!nugetNodeById.TryGetValue(edge.SourceId, out var sourceNode)) continue;
            if (sourceNode.Project == targetProject)
                continue;

            crossEdges.Add(CreateCrossRepoEdge(
                sourceNode.Project, targetProject, edge.SourceId, nugetNode.Id,
                EdgeType.REFERENCES_PACKAGE, new()
                {
                    ["package_name"] = nugetNode.Name,
                    ["version"] = nugetNode.Properties.GetValueOrDefault("version") ?? "",
                    ["confidence_band"] = "high"
                }));
        }

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges);
            _logger.LogInformation("Linked {Count} NuGet package reference(s) across repos", crossEdges.Count);
        }

        return crossEdges.Count;
    }

    /// <summary>
    /// Resolve DEPLOYS and CONFIGURES edges from IaC repos (Ansible/Terraform)
    /// to application repos. Matches by service name, container name, IIS site name,
    /// or image name conventions.
    /// </summary>
    private async Task<int> LinkIacDeploymentsAsync(CancellationToken ct)
    {
        var deployEdges = await _store.FindAllEdgesByTypeAsync(EdgeType.DEPLOYS);
        var configEdges = await _store.FindAllEdgesByTypeAsync(EdgeType.CONFIGURES);

        var allEdges = deployEdges.Concat(configEdges).ToList();
        if (allEdges.Count == 0)
            return 0;

        var projects = await _store.ListRepositoriesAsync();
        var projectNames = projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build lookup: lowercase service/container/site name → project name
        // Convention: TC.OrdersApi → service might be "orders-api", "OrdersApi", "TC.OrdersApi"
        var serviceToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proj in projectNames)
        {
            // "TC.OrdersApi" → match "OrdersApi", "orders-api", "ordersapi", "tc.ordersapi"
            serviceToProject[proj] = proj;
            if (proj.StartsWith("TC.", StringComparison.OrdinalIgnoreCase))
            {
                var shortName = proj[3..]; // "OrdersApi"
                serviceToProject[shortName] = proj;
                serviceToProject[shortName.ToLowerInvariant()] = proj;
                // Kebab-case: "OrdersApi" → "orders-api"
                var kebab = ToKebabCase(shortName);
                serviceToProject[kebab] = proj;
            }
        }

        // Batch-fetch all source nodes
        var sourceNodeIds = allEdges.Select(e => e.SourceId).Distinct().ToList();
        var sourceNodeById = await _store.FindNodesByIdBatchAsync(sourceNodeIds);

        var crossEdges = new List<CrossRepoEdge>();

        foreach (var edge in allEdges)
        {
            if (!sourceNodeById.TryGetValue(edge.SourceId, out var sourceNode)) continue;

            // Try to resolve the target to a known project
            var targetProject = ResolveIacTarget(edge, serviceToProject);
            if (targetProject is null || targetProject == sourceNode.Project)
                continue;

            crossEdges.Add(CreateCrossRepoEdge(
                sourceNode.Project, targetProject, edge.SourceId, edge.TargetId,
                edge.Type == EdgeType.DEPLOYS ? EdgeType.DEPLOYS : EdgeType.CONFIGURES, new()
                {
                    ["confidence_band"] = "medium",
                    ["resolved_from"] = "iac_linker"
                }));
        }

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges);
            _logger.LogInformation("Linked {Count} IaC deployment(s) to application repos", crossEdges.Count);
        }

        return crossEdges.Count;
    }

    private static string? ResolveIacTarget(
        GraphEdge edge, Dictionary<string, string> serviceToProject)
    {
        var props = edge.Properties;

        // Try service_name, iis_site, container, image (in order of confidence)
        foreach (var key in new[] { "service_name", "iis_site", "container" })
        {
            if (props.GetValueOrDefault(key) is string val && !string.IsNullOrEmpty(val))
            {
                if (serviceToProject.TryGetValue(val, out var project))
                    return project;
            }
        }

        // Try image name: "registry.internal/orders-api:latest" → "orders-api"
        if (props.GetValueOrDefault("image") is string image && !string.IsNullOrEmpty(image))
        {
            // Strip tag
            var noTag = image.Contains(':') ? image[..image.LastIndexOf(':')] : image;
            // Strip registry
            var imageName = noTag.Contains('/') ? noTag[(noTag.LastIndexOf('/') + 1)..] : noTag;
            if (serviceToProject.TryGetValue(imageName, out var project))
                return project;
        }

        return null;
    }

    private static string ToKebabCase(string name)
    {
        // "OrdersApi" → "orders-api"
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                result.Append('-');
            result.Append(char.ToLowerInvariant(name[i]));
        }
        return result.ToString();
    }

    private static CrossRepoEdge CreateCrossRepoEdge(
        string sourceProject, string targetProject, long sourceNodeId, long targetNodeId,
        EdgeType type, Dictionary<string, object> properties) => new()
    {
        SourceProject = sourceProject,
        TargetProject = targetProject,
        SourceNodeId = sourceNodeId,
        TargetNodeId = targetNodeId,
        Type = type,
        Properties = properties
    };

    // ── Helpers ──────────────────────────────────────────────────────────

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
    /// Resolve target project for a gateway call. service_name from [TcServiceDto] is always
    /// the Api project name without "TC." prefix (e.g., "DomainBlacklistApi" → "TC.DomainBlacklistApi").
    /// Falls back to namespace-based derivation from the DTO type.
    /// </summary>
    private static string? ResolveGatewayTargetProject(
        string? serviceName, string dtoTypeQN, HashSet<string> projectNames)
    {
        // service_name is always the project name without "TC." prefix
        if (serviceName is not null)
        {
            var candidate = $"TC.{serviceName}";
            if (projectNames.Contains(candidate))
                return candidate;
        }

        // Fall back to namespace convention
        return DerivProjectFromDtoType(dtoTypeQN, projectNames);
    }

    /// <summary>
    /// Derive project name from a request DTO type's namespace.
    /// TC.OrdersApi.Models.GetOrderRequest → TC.OrdersApi
    /// </summary>
    private static string? DerivProjectFromDtoType(string dtoTypeQN, HashSet<string> projectNames)
    {
        // Walk up the namespace: TC.OrdersApi.Models.GetOrderRequest
        // Try: TC.OrdersApi.Models → strip .Models → TC.OrdersApi
        var parts = dtoTypeQN.Split('.');
        for (var i = parts.Length - 1; i >= 2; i--)
        {
            var candidate = string.Join(".", parts[..i]);

            // Try the namespace segment directly
            if (projectNames.Contains(candidate))
                return candidate;

            // Try stripping known suffixes (.Models, .Contracts, .Client)
            foreach (var suffix in new[] { ".Models", ".Contracts", ".Client", ".Shared" })
            {
                if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var stripped = candidate[..^suffix.Length];
                    if (projectNames.Contains(stripped))
                        return stripped;
                }
            }
        }
        return null;
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
