using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

public partial class CrossRepoLinker
{
    internal const string OwnedLinksLockName = ".codegraph/owned-http-npm-links";
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

        var counts = await LinkCoreAsync(scopeRepository: null, ct);

        _logger.LogInformation(
            "Cross-repo linking complete: {Http} HTTP, {Messaging} messaging, {NuGet} NuGet, {Cargo} Cargo, {Swift} Swift package, {Rust} Rust semantic, {Npm} npm export links",
            counts.Http, counts.Messaging, counts.NuGet, counts.Cargo, counts.Swift, counts.Rust, counts.Npm);
    }

    /// <summary>
    /// Incremental cross-repo linking scoped to a single repository: removes this repository's cross-repo
    /// edges, then re-creates the links in which it is the source or the target. Links between two
    /// other repositories are left as those repositories' own runs wrote them.
    /// Called by RepositoryIndexingCompletedConsumer after a repo is indexed.
    /// </summary>
    /// <remarks>
    /// Every pass reads compact projections streamed from the store (linking keys only), never the
    /// full node/edge rows of every repository: once one repository reached ~130k nodes / ~900k edges,
    /// loading all CALLS edges and all definition nodes with their properties for each small repo's
    /// post-index linking exhausted the indexer's managed heap. Passes run sequentially because the
    /// store's DbContext is not thread-safe.
    /// </remarks>
    public async Task LinkForRepositoryAsync(string repository, CancellationToken ct)
    {
        _logger.LogInformation("Running incremental cross-repo linking for {Repository}", repository);

        // HTTP and npm export links own their bounded diffs, including third-pair ambiguity
        // changes. Preserve them for comparison so an unchanged sync retains their identities.
        var existing = await _store.FindCrossRepoEdgesAsync(repository);
        await _store.DeleteCrossRepoEdgesByIdsAsync(existing
            .Where(edge => edge.Type != EdgeType.HTTP_CALLS && !IsNpmExportLink(edge))
            .Select(edge => edge.Id).ToArray(), ct);

        var counts = await LinkCoreAsync(repository, ct);

        _logger.LogInformation(
            "Incremental linking for {Repository} complete: {Http} HTTP, {Messaging} messaging, {NuGet} NuGet, {Cargo} Cargo, {Swift} Swift package, {Rust} Rust semantic, {Npm} npm exports",
            repository, counts.Http, counts.Messaging, counts.NuGet, counts.Cargo, counts.Swift, counts.Rust, counts.Npm);
    }

    private async Task<(int Http, int Messaging, int NuGet, int Cargo, int Swift, int Rust, int Npm)> LinkCoreAsync(
        string? scopeRepository, CancellationToken ct)
    {
        int http, npm;
        // These passes reconcile global uniqueness, including pairs outside the indexed repo.
        // Serialize their complete snapshot→diff interval across processes: an older event
        // must never delete a newer repair using a snapshot it captured before that repair.
        await using (var ownedLinks = await _store.AcquireRepositoryIndexingLockAsync(OwnedLinksLockName, ct))
        {
            http = await LinkHttpRoutesAsync(scopeRepository, ct);
            npm = await LinkNpmExportsAsync(scopeRepository, ct);
        }
        var messaging = await LinkMessagingAsync(scopeRepository, ct);
        var nuget = await LinkNuGetPackagesAsync(scopeRepository, ct);
        var cargo = await LinkManifestPackagesAsync(Cargo, scopeRepository, ct);
        var swift = await LinkManifestPackagesAsync(SwiftPm, scopeRepository, ct);
        var rust = await LinkRustSymbolsAsync(scopeRepository, ct);
        return (http, messaging, nuget, cargo, swift, rust, npm);
    }

    private static bool InScope(string? scopeRepository, string sourceRepository, string targetRepository) =>
        scopeRepository is null ||
        sourceRepository.Equals(scopeRepository, StringComparison.OrdinalIgnoreCase) ||
        targetRepository.Equals(scopeRepository, StringComparison.OrdinalIgnoreCase);

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> rows, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var row in rows.WithCancellation(ct))
            list.Add(row);
        return list;
    }

    private async Task<int> LinkHttpRoutesAsync(string? scopeRepository, CancellationToken ct)
    {
        // HTTP_CALLS edges with their source repository, the target stub's qualified name (it may carry
        // a concrete route repository hint such as route:OrdersApi:GET:api/orders/{id}) and the call keys.
        var httpCallEdges = await ToListAsync(_store.StreamLinkEdgesAsync(
            new LinkEdgeQuery([EdgeType.HTTP_CALLS])
            {
                EdgePropertyKeys = ["gateway_call", "request_dto", "service_name", "url_pattern", "http_method", "semantic_owner", "origin", "http_origin", "url_origin", "origin_pattern", "binding_ambiguous"]
            }, ct), ct);
        if (httpCallEdges.Count == 0)
            return 0;

        // Route keys across all repositories (may be empty for gateway-only calls)
        var routes = await ToListAsync(_store.StreamLinkNodesAsync(
            new LinkNodeQuery([NodeLabel.Route]) { PropertyKeys = HttpRoutePropertyKeys }, ct), ct);

        // Build route lookup: normalize templates for matching
        var routeLookup = BuildHttpRouteLookup(routes);

        var repositories = await _store.ListRepositoriesAsync();
        var repositoryNames = repositories.Select(repository => repository.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await ReconcileLocalHttpBindingsAsync(scopeRepository, httpCallEdges, routeLookup, repositoryNames, ct);
    }

    private async Task<int> LinkMessagingAsync(string? scopeRepository, CancellationToken ct)
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
                if (conEdge.SourceRepository == pubEdge.SourceRepository ||
                    !InScope(scopeRepository, pubEdge.SourceRepository, conEdge.SourceRepository))
                    continue;

                crossEdges.Add(CreateCrossRepoEdge(
                    pubEdge.SourceRepository, conEdge.SourceRepository, pubEdge.SourceId, conEdge.SourceId,
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

    private async Task<int> LinkNuGetPackagesAsync(string? scopeRepository, CancellationToken ct)
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

        // Get all known repositories
        var repositories = await _store.ListRepositoriesAsync();
        var repositoryNames = repositories.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Map package names to repository names when the stripped package name resolves
        // to another indexed repository.
        var packageToRepository = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var crossEdges = new List<CrossRepoEdge>();

        foreach (var edge in refEdges)
        {
            if (!packageToRepository.TryGetValue(edge.TargetName, out var targetRepository))
            {
                var candidateRepository = DerivRepositoryFromPackage(edge.TargetName);
                targetRepository = candidateRepository is not null && repositoryNames.Contains(candidateRepository)
                    ? candidateRepository
                    : null;
                packageToRepository[edge.TargetName] = targetRepository;
            }

            if (targetRepository is null ||
                edge.SourceRepository == targetRepository ||
                !InScope(scopeRepository, edge.SourceRepository, targetRepository))
                continue;

            crossEdges.Add(CreateCrossRepoEdge(
                edge.SourceRepository, targetRepository, edge.SourceId, edge.TargetId,
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
        PackageEcosystem ecosystem, string? scopeRepository, CancellationToken ct)
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

        // Repository URL → repository, for git dependencies. A URL shared by several repositories is ambiguous.
        var gitUrlGroups = (await _store.ListRepositoriesAsync())
            .Select(repository => (repository.Name, Url: NormalizeGitUrl(repository.RepoUrl)))
            .Where(repository => repository.Url is not null)
            .GroupBy(repository => repository.Url!, StringComparer.Ordinal)
            .ToList();
        var repositoriesByGitUrl = gitUrlGroups
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Name, StringComparer.Ordinal);
        var ambiguousGitUrls = gitUrlGroups
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Select(repository => repository.Name).ToList(),
                StringComparer.Ordinal);
        var loggedAmbiguousUrls = new HashSet<string>(StringComparer.Ordinal);

        var crossEdges = new List<CrossRepoEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        // Declared (source repository, target node) pairs, and pinned links held back until every
        // declared one is known.
        var declaredTargets = new HashSet<(string Repository, long TargetId)>();
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
                if (!repositoriesByGitUrl.TryGetValue(gitUrl, out var gitRepository))
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
                    ? RootPackageOf(gitRepository, rootManifest)
                    : definitionsByName.GetValueOrDefault(packageName) ?? [];
                candidates = candidates
                    .Where(candidate => candidate.Repository.Equals(gitRepository, StringComparison.OrdinalIgnoreCase))
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
                    .Where(candidate => segments.Contains(candidate.Repository, StringComparer.OrdinalIgnoreCase))
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

            var targetRepositories = candidates
                .Where(candidate => !candidate.Repository.Equals(
                    edge.SourceRepository, StringComparison.OrdinalIgnoreCase))
                .GroupBy(candidate => candidate.Repository, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targetRepositories.Count != 1)
            {
                if (targetRepositories.Count > 1)
                {
                    _logger.LogDebug(
                        "Skipping ambiguous {Ecosystem} match for {PackageKey}: {RepositoryCount} repositories",
                        ecosystem.DisplayName,
                        packageKey,
                        targetRepositories.Count);
                }
                continue;
            }

            var targetCandidates = targetRepositories[0].ToList();
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
                    "Skipping ambiguous {Ecosystem} match for {PackageKey} within {Repository}",
                    ecosystem.DisplayName,
                    packageKey,
                    targetRepositories[0].Key);
                continue;
            }

            if (!InScope(scopeRepository, edge.SourceRepository, target.Repository))
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
                    declaredTargets.Add((edge.SourceRepository.ToLowerInvariant(), target.Id));
            }

            var link = CreateCrossRepoEdge(
                edge.SourceRepository, target.Repository, edge.SourceId, target.Id, EdgeType.REFERENCES_PACKAGE, properties);
            (isPinned ? pinnedLinks : crossEdges).Add(link);
        }

        // A pin repeats a declared dependency of the same repository (one logical dependency, not
        // two); it is kept only where nothing declares it: an Xcode project's pins, or a
        // transitive pin.
        crossEdges.AddRange(pinnedLinks.Where(link =>
            !declaredTargets.Contains((link.SourceRepository.ToLowerInvariant(), link.TargetNodeId))));

        if (crossEdges.Count > 0)
        {
            await _store.InsertCrossRepoEdgeBatchAsync(crossEdges, ct);
            _logger.LogInformation(
                "Linked {Count} {Ecosystem} reference(s) across repos", crossEdges.Count, ecosystem.DisplayName);
        }

        return crossEdges.Count;

        // SwiftPM resolves a URL dependency to the package at the repository's root.
        IReadOnlyList<LinkNodeRow> RootPackageOf(string repository, string rootManifest) => definitions
            .Where(candidate => candidate.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
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
    /// another repository. Three streamed passes keep memory proportional to the distinct referenced
    /// symbols and their definitions, not to every CALLS edge and definition node in the graph:
    /// (1) collect the distinct referenced symbols, (2) keep only definitions of those symbols,
    /// (3) resolve each reference.
    /// </summary>
    private async Task<int> LinkRustSymbolsAsync(string? scopeRepository, CancellationToken ct)
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

            var targetRepositories = candidates
                .Where(candidate => !candidate.Repository.Equals(
                    edge.SourceRepository, StringComparison.OrdinalIgnoreCase))
                .GroupBy(candidate => candidate.Repository, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targetRepositories.Count != 1)
            {
                if (targetRepositories.Count > 1)
                {
                    _logger.LogDebug(
                        "Skipping ambiguous Rust SCIP symbol match for {Symbol}: {RepositoryCount} repositories",
                        symbol,
                        targetRepositories.Count);
                }
                continue;
            }

            var targetCandidates = targetRepositories[0].ToList();
            if (targetCandidates.Count != 1)
            {
                _logger.LogDebug(
                    "Skipping ambiguous Rust SCIP symbol match for {Symbol} within {Repository}",
                    symbol,
                    targetRepositories[0].Key);
                continue;
            }

            var target = targetCandidates[0];
            if (!InScope(scopeRepository, edge.SourceRepository, target.Repository))
                continue;

            var key = $"{edge.SourceId}\n{target.Id}\n{edge.Type}";
            if (!edgeKeys.Add(key))
                continue;

            crossEdges.Add(CreateCrossRepoEdge(
                edge.SourceRepository,
                target.Repository,
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
        string sourceRepository, string targetRepository, long sourceNodeId, long targetNodeId,
        EdgeType type, Dictionary<string, object> properties) => new()
    {
        SourceRepository = sourceRepository,
        TargetRepository = targetRepository,
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
    internal static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];
    internal static readonly string[] HttpRoutePropertyKeys =
    [
        "http_method", "route_template", "route_kind", "framework", "component", "component_qn",
        "registration_ambiguous", "origin", "http_origin", "url_origin", "service_name"
    ];

    internal static bool IsBackendHttpRoute(LinkNodeRow route)
    {
        if (route.Label != NodeLabel.Route || route.QualifiedName.StartsWith("ui-route:", StringComparison.OrdinalIgnoreCase)) return false;
        var kind = route.Properties.GetValueOrDefault("route_kind")?.ToLowerInvariant();
        if (kind is "ui" or "frontend" or "page" or "screen") return false;
        if (GetBoolean(route.Properties.GetValueOrDefault("registration_ambiguous"))) return false;
        // Legacy Angular pages carried GET plus component metadata before route_kind existed.
        var explicitlyBackend = kind is "api" or "http" or "backend" or "endpoint";
        if (!explicitlyBackend && (!string.IsNullOrWhiteSpace(route.Properties.GetValueOrDefault("component"))
            || !string.IsNullOrWhiteSpace(route.Properties.GetValueOrDefault("component_qn")))) return false;
        var framework = route.Properties.GetValueOrDefault("framework")?.ToLowerInvariant();
        if (!explicitlyBackend && framework is ("angular" or "react" or "react-router" or "next" or "nextjs" or "next.js")) return false;
        return HttpMethods.Contains(route.Properties.GetValueOrDefault("http_method")?.ToUpperInvariant(), StringComparer.Ordinal)
            && route.Properties.ContainsKey("route_template");
    }

    internal static Dictionary<string, List<LinkNodeRow>> BuildHttpRouteLookup(IEnumerable<LinkNodeRow> routes) =>
        routes.Where(IsBackendHttpRoute).DistinctBy(route => (route.Repository, route.QualifiedName))
            .GroupBy(route => NormalizeRouteTemplate(route.Properties.GetValueOrDefault("http_method")!,
                route.Properties.GetValueOrDefault("route_template") ?? ""))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    internal static string NormalizeRouteTemplate(string httpMethod, string template)
    {
        var normalized = HttpPath(template).ToLowerInvariant().TrimStart('/');
        normalized = Regex.Replace(normalized, @"\{\*[^}]+\}", "{**}");
        normalized = Regex.Replace(normalized, @"\{(?!\*\*\})[^}]+\}", "{*}");
        var segments = normalized.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (Regex.IsMatch(segments[i], @"^:[a-z_][a-z0-9_]*(?:\(.+\))?$")) segments[i] = "{*}";
            else if (Regex.IsMatch(segments[i], @"^\*[a-z_][a-z0-9_]*$")) segments[i] = "{**}";
        }
        return $"{httpMethod.ToUpperInvariant()}:{string.Join('/', segments)}";
    }

    internal static string NormalizeUrlForMatching(string httpMethod, string url)
    {
        var segments = HttpPath(url).ToLowerInvariant().TrimStart('/').Split('/');
        for (var i = 0; i < segments.Length; i++)
            if (IsLikelyId(segments[i])) segments[i] = "{*}";
        return $"{httpMethod.ToUpperInvariant()}:{string.Join('/', segments)}";
    }

    private static bool IsLikelyId(string segment) =>
        long.TryParse(segment, out _) || Guid.TryParse(segment, out _)
        || (segment.StartsWith('{') && segment.EndsWith('}'));

    private static string HttpPath(string url)
    {
        var path = Regex.Replace(url.Trim(), @"^https?://[^/?#]+", "", RegexOptions.IgnoreCase);
        var braces = 0;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '{') braces++;
            if (path[i] == '}') braces--;
            if (braces == 0 && path[i] is '?' or '#') return path[..i];
        }
        return path;
    }

    internal static bool IsMatchableHttpCall(string? method, string? url)
    {
        if (method is null || !HttpMethods.Contains(method.ToUpperInvariant(), StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(url)) return false;
        if (Regex.IsMatch(url, @"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(url, @"^https?://", RegexOptions.IgnoreCase)) return false;
        var path = HttpPath(url).Trim('/');
        return path.Length == 0 || path.Split('/').Any(segment => segment.Length > 0
            && segment is not ("*" or "unknown" or "undefined") && !segment.StartsWith('{'));
    }

    internal static string? CallOrigin(IReadOnlyDictionary<string, string?> properties, string url)
    {
        var origin = properties.GetValueOrDefault("origin") ?? properties.GetValueOrDefault("http_origin")
            ?? properties.GetValueOrDefault("url_origin");
        if (string.IsNullOrWhiteSpace(origin))
        {
            var match = Regex.Match(url, @"^https?://[^/?#]+", RegexOptions.IgnoreCase);
            origin = match.Success ? match.Value : null;
        }
        if (string.IsNullOrWhiteSpace(origin)) return null;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') : origin.TrimEnd('/');
    }

    internal static List<LinkNodeRow> FindMatchingRoutes(
        string normalizedCall, string httpMethod, string urlPattern,
        Dictionary<string, List<LinkNodeRow>> routeLookup, Func<LinkNodeRow, bool>? eligible = null)
    {
        eligible ??= _ => true;
        // A concrete static registration takes precedence over a parameterized registration.
        var asTemplate = NormalizeRouteTemplate(httpMethod, urlPattern);
        if (routeLookup.TryGetValue(asTemplate, out var exact) && exact.Where(eligible).ToList() is { Count: > 0 } exactMatches) return exactMatches;
        if (routeLookup.TryGetValue(normalizedCall, out var normalized) && normalized.Where(eligible).ToList() is { Count: > 0 } normalizedMatches) return normalizedMatches;
        var prefix = $"{httpMethod.ToUpperInvariant()}:";
        var callSegments = asTemplate[prefix.Length..].Split('/');
        return routeLookup.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                && MatchesSegments(pair.Key[prefix.Length..].Split('/'), callSegments))
            .SelectMany(pair => pair.Value).Where(eligible).ToList();
    }

    private static bool MatchesSegments(string[] template, string[] call)
    {
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == "{**}") return true;
            if (i >= call.Length || (template[i] != "{*}" && template[i] != call[i])) return false;
        }
        return template.Length == call.Length;
    }

    internal static List<LinkNodeRow> SelectHttpRouteMatches(
        string method, string url, string? serviceName, string? requestDto, string? targetQualifiedName,
        string? origin, HashSet<string> repositoryNames, Dictionary<string, List<LinkNodeRow>> routeLookup)
    {
        if (!IsMatchableHttpCall(method, url)) return [];
        var repositoryHint = ResolveHttpTargetRepository(serviceName, requestDto, targetQualifiedName, repositoryNames);
        // An explicit but unknown service cannot be replaced with an arbitrary path match.
        if (!string.IsNullOrWhiteSpace(serviceName) && repositoryHint is null) return [];
        var originRepository = origin is not null && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            ? ResolveRepositoryName(uri.Host, repositoryNames) : null;
        if (repositoryHint is not null && originRepository is not null
            && !repositoryHint.Equals(originRepository, StringComparison.OrdinalIgnoreCase)) return [];
        bool Eligible(LinkNodeRow route)
        {
            if (repositoryHint is not null && !route.Repository.Equals(repositoryHint, StringComparison.OrdinalIgnoreCase)) return false;
            if (origin is null) return true;
            var routeOrigin = CallOrigin(route.Properties, "");
            if (routeOrigin is not null) return routeOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase);
            return repositoryHint is not null || (originRepository is not null
                && route.Repository.Equals(originRepository, StringComparison.OrdinalIgnoreCase));
        }
        var matches = FindMatchingRoutes(NormalizeUrlForMatching(method, url), method, url, routeLookup, Eligible);
        matches = matches.DistinctBy(route => (route.Repository, route.QualifiedName)).ToList();
        // Multiple route identities in one repository are just as ambiguous as multiple services.
        return matches.Count == 1 ? matches : [];
    }

    /// <summary>
    /// Resolve target repository for a gateway call.
    /// Prefer an exact service-name match, then fall back to namespace-based derivation
    /// from the DTO type.
    /// </summary>
    private static string? ResolveGatewayTargetRepository(
        string? serviceName, string dtoTypeQN, HashSet<string> repositoryNames)
    {
        var serviceRepository = ResolveRepositoryName(serviceName, repositoryNames);
        if (serviceRepository is not null)
            return serviceRepository;

        // Fall back to namespace convention
        return DeriveRepositoryFromDtoType(dtoTypeQN, repositoryNames);
    }

    internal static string? ResolveHttpTargetRepository(
        string? serviceName,
        string? requestDto,
        string? targetQualifiedName,
        HashSet<string> repositoryNames)
    {
        if (!string.IsNullOrWhiteSpace(serviceName))
            return ResolveRepositoryName(serviceName, repositoryNames);

        if (targetQualifiedName is not null)
        {
            var routeRepository = TryGetRepositoryFromRouteQualifiedName(targetQualifiedName);
            if (routeRepository is not null && repositoryNames.Contains(routeRepository))
                return routeRepository;
        }

        return string.IsNullOrWhiteSpace(requestDto)
            ? null
            : DeriveRepositoryFromDtoType(requestDto, repositoryNames);
    }

    private static string? ResolveRepositoryName(string? repositoryName, HashSet<string> repositoryNames)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
            return null;

        if (repositoryNames.Contains(repositoryName))
            return repositoryName;

        var tcRepositoryName = $"TC.{repositoryName}";
        return repositoryNames.Contains(tcRepositoryName)
            ? tcRepositoryName
            : null;
    }

    private static string? TryGetRepositoryFromRouteQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split(':', 4);
        if (parts.Length < 4 || !parts[0].Equals("route", StringComparison.OrdinalIgnoreCase))
            return null;

        var repository = parts[1];
        return string.IsNullOrWhiteSpace(repository) || repository == "*"
            ? null
            : repository;
    }

    /// <summary>
    /// Derive repository name from a request DTO type's namespace.
    /// Example: OrdersApi.Models.GetOrderRequest -> OrdersApi
    /// </summary>
    private static string? DeriveRepositoryFromDtoType(string dtoTypeQN, HashSet<string> repositoryNames)
    {
        // Walk up the namespace segments and try progressively broader candidates.
        // Example: OrdersApi.Models.GetOrderRequest
        // then OrdersApi.Models -> strip .Models -> OrdersApi
        var parts = dtoTypeQN.Split('.');
        for (var i = parts.Length - 1; i >= 2; i--)
        {
            var candidate = string.Join(".", parts[..i]);

            // Try the namespace segment directly
            if (repositoryNames.Contains(candidate))
                return candidate;

            // Try stripping known suffixes (.Models, .Contracts, .Client)
            foreach (var suffix in new[] { ".Models", ".Contracts", ".Client", ".Shared" })
            {
                if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var stripped = candidate[..^suffix.Length];
                    if (repositoryNames.Contains(stripped))
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
    private static string? DerivRepositoryFromPackage(string packageName)
    {
        // Try removing common suffixes: .Models, .Contracts, .Client
        var suffixes = new[] { ".Models", ".Contracts", ".Client", ".Shared" };
        foreach (var suffix in suffixes)
        {
            if (packageName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return packageName[..^suffix.Length];
        }

        // Maybe the package name IS the repository name
        return packageName;
    }
}
