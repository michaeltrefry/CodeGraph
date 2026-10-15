using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

public class CrossRepoLinker
{
    private static readonly IReadOnlyList<NodeLabel> RustDefinitionLabels =
    [
        NodeLabel.Function,
        NodeLabel.Method,
        NodeLabel.Constructor,
        NodeLabel.Class,
        NodeLabel.Struct,
        NodeLabel.Enum,
        NodeLabel.Interface,
        NodeLabel.Namespace,
        NodeLabel.Property
    ];

    private static readonly LinkEdgeQuery RustExternalReferenceQuery =
        new([EdgeType.CALLS, EdgeType.USES_TYPE, EdgeType.IMPLEMENTS])
        {
            TargetLabels = [NodeLabel.ExternalSymbol],
            TargetPropertyEquals = new Dictionary<string, string>
            {
                ["source"] = "scip",
                ["scip_external"] = "true"
            },
            TargetPropertyKeys = ["scip_symbol"]
        };

    private static readonly IReadOnlyDictionary<string, string?> EmptyProperties =
        new Dictionary<string, string?>();

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

        var counts = await LinkCoreAsync(scopeProject: null, ct);

        _logger.LogInformation(
            "Cross-repo linking complete: {Http} HTTP, {Messaging} messaging, {NuGet} NuGet, {Cargo} Cargo, {Swift} Swift package, {Rust} Rust semantic links",
            counts.Http, counts.Messaging, counts.NuGet, counts.Cargo, counts.Swift, counts.Rust);
    }

    /// <summary>
    /// Incremental cross-repo linking scoped to a single project: removes this project's cross-repo
    /// edges, then re-creates the links in which it is the source or the target. Links between two
    /// other projects are left as those projects' own runs wrote them.
    /// Called by RepositoryIndexingCompletedConsumer after a repo is indexed.
    /// </summary>
    /// <remarks>
    /// Every pass reads compact projections streamed from the store (linking keys only), never the
    /// full node/edge rows of every project: once one repository reached ~130k nodes / ~900k edges,
    /// loading all CALLS edges and all definition nodes with their properties for each small repo's
    /// post-index linking exhausted the indexer's managed heap. Passes run sequentially because the
    /// store's DbContext is not thread-safe.
    /// </remarks>
    public async Task LinkForProjectAsync(string project, CancellationToken ct)
    {
        _logger.LogInformation("Running incremental cross-repo linking for {Project}", project);

        // Clear existing cross-repo edges for this project so stale links are removed
        await _store.DeleteCrossRepoEdgesForProjectAsync(project);

        var counts = await LinkCoreAsync(project, ct);

        _logger.LogInformation(
            "Incremental linking for {Project} complete: {Http} HTTP, {Messaging} messaging, {NuGet} NuGet, {Cargo} Cargo, {Swift} Swift package, {Rust} Rust semantic",
            project, counts.Http, counts.Messaging, counts.NuGet, counts.Cargo, counts.Swift, counts.Rust);
    }

    private async Task<(int Http, int Messaging, int NuGet, int Cargo, int Swift, int Rust)> LinkCoreAsync(
        string? scopeProject, CancellationToken ct)
    {
        var http = await LinkHttpRoutesAsync(scopeProject, ct);
        var messaging = await LinkMessagingAsync(scopeProject, ct);
        var nuget = await LinkNuGetPackagesAsync(scopeProject, ct);
        var cargo = await LinkManifestPackagesAsync(Cargo, scopeProject, ct);
        var swift = await LinkManifestPackagesAsync(SwiftPm, scopeProject, ct);
        var rust = await LinkRustSymbolsAsync(scopeProject, ct);
        return (http, messaging, nuget, cargo, swift, rust);
    }

    private static bool InScope(string? scopeProject, string sourceProject, string targetProject) =>
        scopeProject is null ||
        sourceProject.Equals(scopeProject, StringComparison.OrdinalIgnoreCase) ||
        targetProject.Equals(scopeProject, StringComparison.OrdinalIgnoreCase);

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> rows, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var row in rows.WithCancellation(ct))
            list.Add(row);
        return list;
    }

    private async Task<int> LinkHttpRoutesAsync(string? scopeProject, CancellationToken ct)
    {
        // HTTP_CALLS edges with their source project, the target stub's qualified name (it may carry
        // a concrete route project hint such as route:OrdersApi:GET:api/orders/{id}) and the call keys.
        var httpCallEdges = await ToListAsync(_store.StreamLinkEdgesAsync(
            new LinkEdgeQuery([EdgeType.HTTP_CALLS])
            {
                EdgePropertyKeys = ["gateway_call", "request_dto", "service_name", "url_pattern", "http_method"]
            }, ct), ct);
        if (httpCallEdges.Count == 0)
            return 0;

        // Route keys across all projects (may be empty for gateway-only calls)
        var routes = await ToListAsync(_store.StreamLinkNodesAsync(
            new LinkNodeQuery([NodeLabel.Route]) { PropertyKeys = ["http_method", "route_template"] }, ct), ct);

        // Build route lookup: normalize templates for matching
        var routeLookup = routes
            .GroupBy(r => NormalizeRouteTemplate(
                r.Properties.GetValueOrDefault("http_method") ?? "",
                r.Properties.GetValueOrDefault("route_template") ?? ""))
            .ToDictionary(g => g.Key, g => g.ToList());

        var crossEdges = new List<CrossRepoEdge>();

        // Collect all known projects for DTO→project resolution
        var projects = await _store.ListRepositoriesAsync();
        var projectNames = projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in httpCallEdges)
        {
            var isGatewayCall = GetBoolean(edge.EdgeProperties.GetValueOrDefault("gateway_call"));
            var requestDto = edge.EdgeProperties.GetValueOrDefault("request_dto");

            if (isGatewayCall && requestDto is not null && !requestDto.StartsWith("route:"))
            {
                var serviceName = edge.EdgeProperties.GetValueOrDefault("service_name");
                var targetProject = ResolveGatewayTargetProject(serviceName, requestDto, projectNames);
                if (targetProject is null)
                    continue;

                if (edge.SourceProject == targetProject ||
                    !InScope(scopeProject, edge.SourceProject, targetProject))
                    continue;

                crossEdges.Add(CreateCrossRepoEdge(
                    edge.SourceProject, targetProject, edge.SourceId, edge.TargetId,
                    EdgeType.HTTP_CALLS, new()
                    {
                        ["request_dto"] = requestDto,
                        ["gateway_call"] = true,
                        ["confidence_band"] = "medium"
                    }));
                continue;
            }

            // Standard route-based HTTP_CALLS matching
            var urlPattern = edge.EdgeProperties.GetValueOrDefault("url_pattern");
            var httpMethod = edge.EdgeProperties.GetValueOrDefault("http_method");

            if (urlPattern is null || httpMethod is null)
                continue;

            var normalizedCall = NormalizeUrlForMatching(httpMethod, urlPattern);

            // Try exact match first, then pattern match
            var matchedRoutes = FindMatchingRoutes(normalizedCall, httpMethod, urlPattern, routeLookup);
            var targetProjectHint = ResolveHttpTargetProject(
                edge.EdgeProperties.GetValueOrDefault("service_name"),
                requestDto,
                edge.TargetQualifiedName,
                projectNames);

            if (targetProjectHint is not null)
            {
                matchedRoutes = matchedRoutes
                    .Where(route => route.Project.Equals(targetProjectHint, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else
            {
                var matchingProjects = matchedRoutes
                    .Select(route => route.Project)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (matchingProjects.Count > 1)
                {
                    _logger.LogDebug(
                        "Skipping ambiguous HTTP route match for {Method} {UrlPattern}: matched {ProjectCount} projects ({Projects})",
                        httpMethod,
                        urlPattern,
                        matchingProjects.Count,
                        string.Join(", ", matchingProjects.Take(10)));
                    continue;
                }
            }

            foreach (var route in matchedRoutes)
            {
                if (edge.SourceProject == route.Project ||
                    !InScope(scopeProject, edge.SourceProject, route.Project))
                    continue;

                crossEdges.Add(CreateCrossRepoEdge(
                    edge.SourceProject, route.Project, edge.SourceId, route.Id,
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
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges, ct);
            _logger.LogInformation("Linked {Count} HTTP call(s) to routes across repos", crossEdges.Count);
        }

        return crossEdges.Count;
    }

    private async Task<int> LinkMessagingAsync(string? scopeProject, CancellationToken ct)
    {
        // Group consumers by the event type QN they consume
        var consumersByEvent = new Dictionary<string, List<LinkEdgeRow>>();
        await foreach (var edge in _store.StreamLinkEdgesAsync(
                           new LinkEdgeQuery([EdgeType.CONSUMES]), ct).WithCancellation(ct))
        {
            if (!consumersByEvent.TryGetValue(edge.TargetQualifiedName, out var list))
            {
                list = new List<LinkEdgeRow>();
                consumersByEvent[edge.TargetQualifiedName] = list;
            }
            list.Add(edge);
        }

        if (consumersByEvent.Count == 0)
            return 0;

        var crossEdges = new List<CrossRepoEdge>();

        await foreach (var pubEdge in _store.StreamLinkEdgesAsync(
                           new LinkEdgeQuery([EdgeType.PUBLISHES]), ct).WithCancellation(ct))
        {
            if (!consumersByEvent.TryGetValue(pubEdge.TargetQualifiedName, out var consumers))
                continue;

            foreach (var conEdge in consumers)
            {
                if (conEdge.SourceProject == pubEdge.SourceProject ||
                    !InScope(scopeProject, pubEdge.SourceProject, conEdge.SourceProject))
                    continue;

                crossEdges.Add(CreateCrossRepoEdge(
                    pubEdge.SourceProject, conEdge.SourceProject, pubEdge.SourceId, conEdge.SourceId,
                    EdgeType.PUBLISHES, new()
                    {
                        ["event_type"] = pubEdge.TargetQualifiedName,
                        ["confidence_band"] = "high"
                    }));
            }
        }

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges, ct);
            _logger.LogInformation("Linked {Count} publisher→consumer pair(s) across repos", crossEdges.Count);
        }

        return crossEdges.Count;
    }

    private async Task<int> LinkNuGetPackagesAsync(string? scopeProject, CancellationToken ct)
    {
        // REFERENCES_PACKAGE edges whose target is a NuGetPackage node
        var refEdges = await ToListAsync(_store.StreamLinkEdgesAsync(
            new LinkEdgeQuery([EdgeType.REFERENCES_PACKAGE])
            {
                TargetLabels = [NodeLabel.NuGetPackage],
                TargetPropertyKeys = ["version"]
            }, ct), ct);
        if (refEdges.Count == 0)
            return 0;

        // Get all known projects
        var projects = await _store.ListRepositoriesAsync();
        var projectNames = projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Map package names to project names when the stripped package name resolves
        // to another indexed repository.
        var packageToProject = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var crossEdges = new List<CrossRepoEdge>();

        foreach (var edge in refEdges)
        {
            if (!packageToProject.TryGetValue(edge.TargetName, out var targetProject))
            {
                var candidateProject = DerivProjectFromPackage(edge.TargetName);
                targetProject = candidateProject is not null && projectNames.Contains(candidateProject)
                    ? candidateProject
                    : null;
                packageToProject[edge.TargetName] = targetProject;
            }

            if (targetProject is null ||
                edge.SourceProject == targetProject ||
                !InScope(scopeProject, edge.SourceProject, targetProject))
                continue;

            crossEdges.Add(CreateCrossRepoEdge(
                edge.SourceProject, targetProject, edge.SourceId, edge.TargetId,
                EdgeType.REFERENCES_PACKAGE, new()
                {
                    ["package_name"] = edge.TargetName,
                    ["version"] = edge.TargetProperties.GetValueOrDefault("version") ?? "",
                    ["confidence_band"] = "high"
                }));
        }

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges, ct);
            _logger.LogInformation("Linked {Count} NuGet package reference(s) across repos", crossEdges.Count);
        }

        return crossEdges.Count;
    }

    /// <summary>How one manifest ecosystem's dependency references are matched to definitions.</summary>
    /// <param name="Ecosystem">The <c>ecosystem</c> property of the Package nodes.</param>
    /// <param name="DisplayName">Name used in log messages.</param>
    /// <param name="RemoteSourceKind">The <c>source_kind</c> of a dependency fetched from a git URL.</param>
    /// <param name="RemoteUrlProperty">The dependency property holding that URL as written.</param>
    /// <param name="RootManifest">
    /// When set, a URL dependency resolves to the package defined by the repository's root manifest
    /// of this name (SwiftPM); otherwise to the definition of the dependency's package name (Cargo).
    /// </param>
    /// <param name="LocalNameProperty">Dependency property copied onto the link (default: the node name).</param>
    /// <param name="DependencyKindProperty">
    /// When set, the dependency property saying whether a reference is <c>declared</c> in a manifest
    /// or <c>pinned</c> in a lock file. A pinned reference is linked only when the same repository
    /// declares no dependency on the same package (an Xcode project without a manifest, or a
    /// transitive pin), and the kind is copied onto the link.
    /// </param>
    /// <param name="PathMatchNeedsRepositoryOnPath">
    /// When true, a path dependency that leaves its repository links only to a repository named
    /// like a directory on the path: the dependency's package name is just the directory name, not
    /// an authoritative package name (SwiftPM).
    /// </param>
    private sealed record PackageEcosystem(
        string Ecosystem,
        string DisplayName,
        string RemoteSourceKind,
        string RemoteUrlProperty,
        string? RootManifest,
        string LocalNameProperty,
        string? DependencyKindProperty,
        bool PathMatchNeedsRepositoryOnPath);

    private static readonly PackageEcosystem Cargo = new(
        "cargo", "Cargo package", RemoteSourceKind: "git", RemoteUrlProperty: "source",
        RootManifest: null, LocalNameProperty: "local_name",
        DependencyKindProperty: null, PathMatchNeedsRepositoryOnPath: false);

    private static readonly PackageEcosystem SwiftPm = new(
        "swiftpm", "Swift package", RemoteSourceKind: "remote", RemoteUrlProperty: "location",
        RootManifest: "Package.swift", LocalNameProperty: "identity",
        DependencyKindProperty: "dependency_kind", PathMatchNeedsRepositoryOnPath: true);

    /// <summary>
    /// Links manifest dependency references to the package definition in another indexed
    /// repository: by exact package key (registry crates), by the dependency's git URL matching a
    /// repository's URL, or — for a path dependency that leaves its repository — by package name.
    /// </summary>
    private async Task<int> LinkManifestPackagesAsync(
        PackageEcosystem ecosystem, string? scopeProject, CancellationToken ct)
    {
        var definitions = await ToListAsync(_store.StreamLinkNodesAsync(
            new LinkNodeQuery([NodeLabel.Package])
            {
                PropertyEquals = new Dictionary<string, string>
                {
                    ["ecosystem"] = ecosystem.Ecosystem,
                    ["is_definition"] = "true"
                },
                PropertyKeys = ["package_key", "version", "manifest_path"]
            }, ct), ct);
        if (definitions.Count == 0)
            return 0;

        var definitionsByKey = definitions
            .Where(node => !string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("package_key")))
            .GroupBy(node => node.Properties["package_key"]!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var definitionsByName = definitions
            .GroupBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        // Repository URL → project, for git dependencies. A URL shared by several projects is ambiguous.
        var repositoriesByGitUrl = (await _store.ListRepositoriesAsync())
            .Select(repository => (repository.Name, Url: NormalizeGitUrl(repository.RepoUrl)))
            .Where(repository => repository.Url is not null)
            .GroupBy(repository => repository.Url!, StringComparer.Ordinal)
            .ToList();
        var projectsByGitUrl = repositoriesByGitUrl
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Name, StringComparer.Ordinal);
        var ambiguousGitUrls = repositoriesByGitUrl
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Select(repository => repository.Name).ToList(),
                StringComparer.Ordinal);
        var loggedAmbiguousUrls = new HashSet<string>(StringComparer.Ordinal);

        var crossEdges = new List<CrossRepoEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        // Declared (source project, target node) pairs, and pinned links held back until every
        // declared one is known.
        var declaredTargets = new HashSet<(string Project, long TargetId)>();
        var pinnedLinks = new List<CrossRepoEdge>();
        string[] targetPropertyKeys =
        [
            "package_key", "version", "package_name", ecosystem.LocalNameProperty,
            "source_kind", "source", ecosystem.RemoteUrlProperty, "external_path"
        ];
        if (ecosystem.DependencyKindProperty is { } dependencyKindKey)
            targetPropertyKeys = [.. targetPropertyKeys, dependencyKindKey];

        await foreach (var edge in _store.StreamLinkEdgesAsync(
                           new LinkEdgeQuery([EdgeType.REFERENCES_PACKAGE])
                           {
                               TargetLabels = [NodeLabel.Package],
                               TargetPropertyEquals = new Dictionary<string, string> { ["ecosystem"] = ecosystem.Ecosystem },
                               TargetPropertyKeys = targetPropertyKeys
                           }, ct).WithCancellation(ct))
        {
            var dependency = edge.TargetProperties;
            var packageKey = dependency.GetValueOrDefault("package_key");
            if (string.IsNullOrWhiteSpace(packageKey))
                continue;

            var packageName = dependency.GetValueOrDefault("package_name") ?? edge.TargetName;
            var sourceKind = dependency.GetValueOrDefault("source_kind");
            IReadOnlyList<LinkNodeRow> candidates;
            string matchedBy;
            string confidenceBand;
            if (definitionsByKey.TryGetValue(packageKey, out var keyed))
            {
                candidates = keyed;
                matchedBy = "package_key";
                confidenceBand = "high";
            }
            else if (sourceKind == ecosystem.RemoteSourceKind)
            {
                // The repository at the dependency's git URL defines the package under its own key.
                var gitUrl = NormalizeGitUrl(dependency.GetValueOrDefault(ecosystem.RemoteUrlProperty));
                if (gitUrl is null)
                    continue;
                if (!projectsByGitUrl.TryGetValue(gitUrl, out var gitProject))
                {
                    if (ambiguousGitUrls.TryGetValue(gitUrl, out var sharing) && loggedAmbiguousUrls.Add(gitUrl))
                    {
                        _logger.LogInformation(
                            "Not linking {Ecosystem} dependencies on {GitUrl}: the URL is the remote of several repositories ({Repositories})",
                            ecosystem.DisplayName,
                            gitUrl,
                            string.Join(", ", sharing));
                    }
                    continue;
                }

                candidates = ecosystem.RootManifest is { } rootManifest
                    ? RootPackageOf(gitProject, rootManifest)
                    : definitionsByName.GetValueOrDefault(packageName) ?? [];
                candidates = candidates
                    .Where(candidate => candidate.Project.Equals(gitProject, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count == 0)
                    continue;
                matchedBy = "git_url";
                confidenceBand = "high";
            }
            else if (sourceKind == "path" &&
                     dependency.GetValueOrDefault("external_path") is { Length: > 0 } externalPath &&
                     definitionsByName.TryGetValue(packageName, out var named))
            {
                // A path outside the repository names no repository, only a checkout directory:
                // match the package name, preferring a repository named like a directory on the path.
                var segments = externalPath.Split('/');
                var onPath = named
                    .Where(candidate => segments.Contains(candidate.Project, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (onPath.Count == 0 && ecosystem.PathMatchNeedsRepositoryOnPath)
                    continue;
                candidates = onPath.Count > 0 ? onPath : named;
                matchedBy = "path";
                confidenceBand = "medium";
            }
            else
            {
                continue;
            }

            var targetProjects = candidates
                .Where(candidate => !candidate.Project.Equals(
                    edge.SourceProject, StringComparison.OrdinalIgnoreCase))
                .GroupBy(candidate => candidate.Project, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targetProjects.Count != 1)
            {
                if (targetProjects.Count > 1)
                {
                    _logger.LogDebug(
                        "Skipping ambiguous {Ecosystem} match for {PackageKey}: {ProjectCount} projects",
                        ecosystem.DisplayName,
                        packageKey,
                        targetProjects.Count);
                }
                continue;
            }

            var targetCandidates = targetProjects[0].ToList();
            var requestedVersion = dependency.GetValueOrDefault("version");
            var exactVersionCandidates = string.IsNullOrWhiteSpace(requestedVersion)
                ? targetCandidates
                : targetCandidates
                    .Where(candidate => string.Equals(
                        candidate.Properties.GetValueOrDefault("version"),
                        requestedVersion,
                        StringComparison.Ordinal))
                    .ToList();
            var target = exactVersionCandidates.Count == 1
                ? exactVersionCandidates[0]
                : targetCandidates.Count == 1
                    ? targetCandidates[0]
                    : null;
            if (target is null)
            {
                _logger.LogDebug(
                    "Skipping ambiguous {Ecosystem} match for {PackageKey} within {Project}",
                    ecosystem.DisplayName,
                    packageKey,
                    targetProjects[0].Key);
                continue;
            }

            if (!InScope(scopeProject, edge.SourceProject, target.Project))
                continue;

            var key = $"{edge.SourceId}\n{target.Id}\n{EdgeType.REFERENCES_PACKAGE}";
            if (!edgeKeys.Add(key))
                continue;

            var properties = new Dictionary<string, object>
            {
                ["ecosystem"] = ecosystem.Ecosystem,
                ["package_key"] = packageKey,
                ["package_name"] = packageName,
                [ecosystem.LocalNameProperty] = dependency.GetValueOrDefault(ecosystem.LocalNameProperty) ?? edge.TargetName,
                ["version"] = requestedVersion ?? "",
                ["matched_by"] = matchedBy,
                ["confidence_band"] = confidenceBand
            };
            var isPinned = false;
            if (ecosystem.DependencyKindProperty is { } kindProperty)
            {
                var kind = dependency.GetValueOrDefault(kindProperty) ?? "declared";
                properties[kindProperty] = kind;
                isPinned = kind == "pinned";
                if (!isPinned)
                    declaredTargets.Add((edge.SourceProject.ToLowerInvariant(), target.Id));
            }

            var link = CreateCrossRepoEdge(
                edge.SourceProject, target.Project, edge.SourceId, target.Id, EdgeType.REFERENCES_PACKAGE, properties);
            (isPinned ? pinnedLinks : crossEdges).Add(link);
        }

        // A pin repeats a declared dependency of the same repository (one logical dependency, not
        // two); it is kept only where nothing declares it: an Xcode project's pins, or a
        // transitive pin.
        crossEdges.AddRange(pinnedLinks.Where(link =>
            !declaredTargets.Contains((link.SourceProject.ToLowerInvariant(), link.TargetNodeId))));

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges, ct);
            _logger.LogInformation(
                "Linked {Count} {Ecosystem} reference(s) across repos", crossEdges.Count, ecosystem.DisplayName);
        }

        return crossEdges.Count;

        // SwiftPM resolves a URL dependency to the package at the repository's root.
        IReadOnlyList<LinkNodeRow> RootPackageOf(string project, string rootManifest) => definitions
            .Where(candidate => candidate.Project.Equals(project, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(candidate.Properties.GetValueOrDefault("manifest_path"),
                                    rootManifest, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// A git remote as <c>host/path</c>, lowercase, without scheme, user, port, <c>.git</c> or
    /// trailing slash, so https, ssh and scp-style spellings of one repository compare equal.
    /// </summary>
    internal static string? NormalizeGitUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var value = url.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            value = value[(scheme + 3)..];
        else if (value.IndexOf(':') is var colon and > 0 && !value[..colon].Contains('/'))
            value = $"{value[..colon]}/{value[(colon + 1)..]}"; // scp style: git@host:owner/repo

        var slash = value.IndexOf('/');
        if (slash <= 0)
            return null;
        var host = value[..slash];
        host = host[(host.LastIndexOf('@') + 1)..];
        if (host.IndexOf(':') is var port and > 0)
            host = host[..port];

        var path = value[(slash + 1)..];
        var end = path.IndexOfAny(['?', '#']);
        if (end >= 0)
            path = path[..end];
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4].TrimEnd('/');

        return host.Length == 0 || path.Length == 0
            ? null
            : $"{host}/{path}".ToLowerInvariant();
    }

    /// <summary>
    /// Links references to SCIP external symbols to the one definition carrying the same symbol in
    /// another project. Three streamed passes keep memory proportional to the distinct referenced
    /// symbols and their definitions, not to every CALLS edge and definition node in the graph:
    /// (1) collect the distinct referenced symbols, (2) keep only definitions of those symbols,
    /// (3) resolve each reference.
    /// </summary>
    private async Task<int> LinkRustSymbolsAsync(string? scopeProject, CancellationToken ct)
    {
        var referencedSymbols = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var edge in _store.StreamLinkEdgesAsync(RustExternalReferenceQuery, ct).WithCancellation(ct))
        {
            var symbol = edge.TargetProperties.GetValueOrDefault("scip_symbol");
            if (!string.IsNullOrWhiteSpace(symbol))
                referencedSymbols.Add(symbol);
        }
        if (referencedSymbols.Count == 0)
            return 0;

        var definitionsBySymbol = new Dictionary<string, List<LinkNodeRow>>(StringComparer.Ordinal);
        await foreach (var node in _store.StreamLinkNodesAsync(
                           new LinkNodeQuery(RustDefinitionLabels)
                           {
                               PropertyEquals = new Dictionary<string, string> { ["source"] = "scip" },
                               PropertyKeys = ["scip_symbol", "scip_external"]
                           }, ct).WithCancellation(ct))
        {
            if (GetBoolean(node.Properties.GetValueOrDefault("scip_external")))
                continue;
            var symbol = node.Properties.GetValueOrDefault("scip_symbol");
            if (string.IsNullOrWhiteSpace(symbol) || !referencedSymbols.TryGetValue(symbol, out var shared))
                continue;

            if (!definitionsBySymbol.TryGetValue(shared, out var list))
            {
                list = new List<LinkNodeRow>(1);
                definitionsBySymbol[shared] = list;
            }
            list.Add(node with { Properties = EmptyProperties });
        }
        referencedSymbols.Clear();
        if (definitionsBySymbol.Count == 0)
            return 0;

        var crossEdges = new List<CrossRepoEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var edge in _store.StreamLinkEdgesAsync(RustExternalReferenceQuery, ct).WithCancellation(ct))
        {
            var symbol = edge.TargetProperties.GetValueOrDefault("scip_symbol");
            if (string.IsNullOrWhiteSpace(symbol) ||
                !definitionsBySymbol.TryGetValue(symbol, out var candidates))
            {
                continue;
            }

            var targetProjects = candidates
                .Where(candidate => !candidate.Project.Equals(
                    edge.SourceProject, StringComparison.OrdinalIgnoreCase))
                .GroupBy(candidate => candidate.Project, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targetProjects.Count != 1)
            {
                if (targetProjects.Count > 1)
                {
                    _logger.LogDebug(
                        "Skipping ambiguous Rust SCIP symbol match for {Symbol}: {ProjectCount} projects",
                        symbol,
                        targetProjects.Count);
                }
                continue;
            }

            var targetCandidates = targetProjects[0].ToList();
            if (targetCandidates.Count != 1)
            {
                _logger.LogDebug(
                    "Skipping ambiguous Rust SCIP symbol match for {Symbol} within {Project}",
                    symbol,
                    targetProjects[0].Key);
                continue;
            }

            var target = targetCandidates[0];
            if (!InScope(scopeProject, edge.SourceProject, target.Project))
                continue;

            var key = $"{edge.SourceId}\n{target.Id}\n{edge.Type}";
            if (!edgeKeys.Add(key))
                continue;

            crossEdges.Add(CreateCrossRepoEdge(
                edge.SourceProject,
                target.Project,
                edge.SourceId,
                target.Id,
                edge.Type,
                new()
                {
                    ["source"] = "scip",
                    ["scip_symbol"] = symbol,
                    ["confidence"] = 0.95,
                    ["confidence_band"] = "high"
                }));
        }

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges, ct);
            _logger.LogInformation(
                "Linked {Count} Rust semantic reference(s) across repos", crossEdges.Count);
        }

        return crossEdges.Count;
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

    private static bool GetBoolean(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

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

    private static List<LinkNodeRow> FindMatchingRoutes(
        string normalizedCall, string httpMethod, string urlPattern,
        Dictionary<string, List<LinkNodeRow>> routeLookup)
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
    /// Resolve target project for a gateway call.
    /// Prefer an exact service-name match, then fall back to namespace-based derivation
    /// from the DTO type.
    /// </summary>
    private static string? ResolveGatewayTargetProject(
        string? serviceName, string dtoTypeQN, HashSet<string> projectNames)
    {
        var serviceProject = ResolveProjectName(serviceName, projectNames);
        if (serviceProject is not null)
            return serviceProject;

        // Fall back to namespace convention
        return DeriveProjectFromDtoType(dtoTypeQN, projectNames);
    }

    private static string? ResolveHttpTargetProject(
        string? serviceName,
        string? requestDto,
        string? targetQualifiedName,
        HashSet<string> projectNames)
    {
        var serviceProject = ResolveProjectName(serviceName, projectNames);
        if (serviceProject is not null)
            return serviceProject;

        if (targetQualifiedName is not null)
        {
            var routeProject = TryGetProjectFromRouteQualifiedName(targetQualifiedName);
            if (routeProject is not null && projectNames.Contains(routeProject))
                return routeProject;
        }

        return string.IsNullOrWhiteSpace(requestDto)
            ? null
            : DeriveProjectFromDtoType(requestDto, projectNames);
    }

    private static string? ResolveProjectName(string? projectName, HashSet<string> projectNames)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return null;

        if (projectNames.Contains(projectName))
            return projectName;

        var tcProjectName = $"TC.{projectName}";
        return projectNames.Contains(tcProjectName)
            ? tcProjectName
            : null;
    }

    private static string? TryGetProjectFromRouteQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split(':', 4);
        if (parts.Length < 4 || !parts[0].Equals("route", StringComparison.OrdinalIgnoreCase))
            return null;

        var project = parts[1];
        return string.IsNullOrWhiteSpace(project) || project == "*"
            ? null
            : project;
    }

    /// <summary>
    /// Derive project name from a request DTO type's namespace.
    /// Example: OrdersApi.Models.GetOrderRequest -> OrdersApi
    /// </summary>
    private static string? DeriveProjectFromDtoType(string dtoTypeQN, HashSet<string> projectNames)
    {
        // Walk up the namespace segments and try progressively broader candidates.
        // Example: OrdersApi.Models.GetOrderRequest
        // then OrdersApi.Models -> strip .Models -> OrdersApi
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
    /// Example:
    /// OrdersApi.Models -> OrdersApi
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
