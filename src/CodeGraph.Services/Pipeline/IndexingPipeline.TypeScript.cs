using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// TypeScript project analysis scope (sc-26145): the Node.js sidecar discovers tsconfig files,
/// workspace packages and sources itself, so it is handed exactly the files the pipeline's
/// discovery admits, its result is clamped to them, and graph slices it left behind under
/// excluded directories (agent worktrees, dependency directories) are removed.
/// </summary>
public partial class IndexingPipeline
{
    private const string TypeScriptConfigFileName = "tsconfig.json";
    private const string TypeScriptWorkspaceModuleKind = "typescript_workspace_package";

    /// <summary>
    /// Repo-relative files the TypeScript sidecar may read: sources, tsconfig.json and
    /// package.json files that no discovery exclusion (skip pattern or dependency directory) hides.
    /// </summary>
    private IReadOnlyList<string> DiscoverTypeScriptScope(string rootPath) =>
        DiscoverPaths(rootPath, "**/*.js", "**/*.jsx", "**/*.ts", "**/*.tsx", "**/*.html",
                "**/tsconfig*.json", "**/jsconfig*.json", "**/package.json", "**/angular.json", "**/next.config.*", "**/vite.config.*",
                "**/*.rs", "**/Cargo.toml", "**/tauri.conf.json", "**/tauri.conf.json5", "**/Tauri.toml")
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool IsFrontendReadInput(string path) => IsTypeScriptPath(path)
        || Path.GetFileName(path) is "package.json" or "angular.json"
        || Path.GetFileName(path).StartsWith("tsconfig", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)
        || Path.GetFileName(path).StartsWith("jsconfig", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)
        || Path.GetFileName(path).StartsWith("next.config.", StringComparison.Ordinal)
        || Path.GetFileName(path).StartsWith("vite.config.", StringComparison.Ordinal);

    // Preflight before ordinary source hashing/LOC reads. HTML is deliberately left unread:
    // only the sidecar knows which admitted templates a component actually references.
    private async Task<IReadOnlyDictionary<string, RegistrationSourceReader.Source>> ReadFrontendInputsAsync(
        string root, IReadOnlyList<string> scope, CancellationToken ct)
    {
        var sources = new Dictionary<string, RegistrationSourceReader.Source>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (_typeScriptAnalyzer is null || !scope.Any(IsTypeScriptPath)) return sources;
        try
        {
            TypeScriptSourceLimits.ValidateScope(scope, ct);
            var inputs = scope.Where(IsFrontendReadInput).ToList();
            if (inputs.Count > TypeScriptSourceLimits.MaxReadFiles)
                throw new InvalidOperationException("Frontend source/config file count exceeds its bounded analysis limit.");
            var reader = new RegistrationSourceReader(_fileSystem, _options, root, _options.MaxFrontendAnalysisTextKb);
            foreach (var path in inputs)
            {
                var full = Path.GetFullPath(Path.Combine(root, path));
                sources[full] = await reader.ReadAsync(full, ct)
                    ?? throw new InvalidOperationException($"Required frontend source/config {path} exceeds its text limit.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Frontend input admission incomplete; preserving prior graph and source hashes");
            throw new InvalidOperationException("Frontend analysis incomplete; no graph or hash replacement was committed.", ex);
        }
        return sources;
    }

    /// <summary>
    /// The tsconfig.json whose compiler options the sidecar starts from: the root one, else the
    /// shallowest one discovery admits. The sidecar scans sources from the scope, not from it.
    /// </summary>
    private static string? SelectTypeScriptConfig(IReadOnlyList<string> scope) =>
        scope.Where(path => Path.GetFileName(path) is TypeScriptConfigFileName or "jsconfig.json")
            .OrderBy(path => path.Count(ch => ch == '/'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// <paramref name="result"/> without anything discovery excludes: nodes whose file (or, for
    /// workspace package modules, directory) is excluded, edges and unresolved references
    /// touching them or an excluded file, and excluded processed files. Guards the graph against
    /// a sidecar that reads more than it was given.
    /// </summary>
    private ExtractionResult ClampToDiscovery(ExtractionResult result, string repositoryName, string rootPath)
    {
        var excludedPath = CreateDiscoveryExclusion(rootPath);
        bool Admitted(string? filePath) => string.IsNullOrWhiteSpace(filePath) || !excludedPath(filePath);

        // A copy can share its qualified name with the admitted original (a vendored package),
        // so nodes are dropped individually and edges only for names no admitted node keeps.
        var kept = result.Nodes.Where(node => Admitted(node.FilePath)
            && Admitted(TypeScriptNodeText(node.Properties.GetValueOrDefault("extraction_source_file")))).ToList();
        var keptQns = kept.Select(node => node.QualifiedName).ToHashSet(StringComparer.Ordinal);
        var dropped = result.Nodes
            .Select(node => node.QualifiedName)
            .Where(qualifiedName => !keptQns.Contains(qualifiedName))
            .ToHashSet(StringComparer.Ordinal);
        // JS/TS declaration identities include a lexical suffix after their source file (and
        // legal source filenames may themselves contain '#'). Trust exact kept declarations
        // with a concrete admitted owner instead of interpreting the whole identity as a path.
        var admittedDeclarationQns = kept.Where(node => !string.IsNullOrWhiteSpace(node.FilePath))
            .Select(node => node.QualifiedName).ToHashSet(StringComparer.Ordinal);
        var filePrefix = $"{repositoryName}:";
        bool AdmittedFileQn(string qualifiedName) =>
            !qualifiedName.StartsWith(filePrefix, StringComparison.Ordinal)
            || admittedDeclarationQns.Contains(qualifiedName)
            // Structural file references retain their literal path, with no lexical guessing.
            || Admitted(qualifiedName[filePrefix.Length..]);
        var clamped = result with
        {
            Nodes = kept,
            Edges = result.Edges
                .Where(edge => !dropped.Contains(edge.SourceQN) && !dropped.Contains(edge.TargetQN)
                               && AdmittedFileQn(edge.SourceQN) && AdmittedFileQn(edge.TargetQN)
                               && Admitted(TypeScriptNodeText(edge.Properties?.GetValueOrDefault("extraction_source_file"))))
                .ToList(),
            UnresolvedCalls = result.UnresolvedCalls.Where(call => !dropped.Contains(call.CallerQN)).ToList(),
            UnresolvedImports = result.UnresolvedImports.Where(import => AdmittedFileQn(import.FileQN)).ToList(),
            ProcessedFiles = result.ProcessedFiles.Where(Admitted).ToList()
        };
        var removedNodes = result.Nodes.Count - clamped.Nodes.Count;
        var removedEdges = result.Edges.Count - clamped.Edges.Count;
        var removedFiles = result.ProcessedFiles.Count - clamped.ProcessedFiles.Count;
        if (removedNodes + removedEdges + removedFiles == 0
            && clamped.UnresolvedCalls.Count == result.UnresolvedCalls.Count
            && clamped.UnresolvedImports.Count == result.UnresolvedImports.Count)
            return result;

        _logger.LogWarning(
            "TypeScript analysis of {Repository} returned {Nodes} node(s), {Edges} edge(s) and {Files} file(s) under paths discovery excludes; dropping them",
            repositoryName, removedNodes, removedEdges, removedFiles);
        return clamped;
    }

    /// <summary>
    /// Paths of the TypeScript workspace package slices to replace on an incremental run: a
    /// workspace package <c>Module</c> node is keyed by its package directory, which is never a
    /// hash-tracked file, so a package that appeared, moved or disappeared is written or removed
    /// here. Unchanged packages are left alone.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindChangedTypeScriptModuleSlicesAsync(
        string repositoryName, IEnumerable<GraphNode> analyzedNodes)
    {
        static IEnumerable<(string QualifiedName, string FilePath)> Modules(IEnumerable<GraphNode> nodes) =>
            WorkspaceModules(nodes)
                .Where(node => !string.IsNullOrWhiteSpace(node.FilePath))
                .Select(node => (node.QualifiedName, NormalizeRelativePath(node.FilePath!)));

        var analyzed = analyzedNodes.ToList();
        var current = Modules(analyzed).ToHashSet();
        var storedNodes = await _store.FindNodesByLabelAsync(repositoryName, NodeLabel.Module, int.MaxValue);
        var stored = Modules(storedNodes).ToHashSet();
        var paths = current.Except(stored).Concat(stored.Except(current))
            .Select(module => module.FilePath)
            .ToList();

        // A root module (no directory) the analysis no longer yields has no slice to replace: it is
        // moved onto a reserved slice that this write then replaces with nothing.
        var currentRoots = WorkspaceModules(analyzed)
            .Where(node => string.IsNullOrWhiteSpace(node.FilePath))
            .DistinctBy(node => node.QualifiedName)
            .ToDictionary(node => node.QualifiedName, StringComparer.Ordinal);
        var staleRoots = WorkspaceModules(storedNodes)
            .Where(node => string.IsNullOrWhiteSpace(node.FilePath)
                && (!currentRoots.TryGetValue(node.QualifiedName, out var currentRoot)
                    || TypeScriptNodeSignature(node) != TypeScriptNodeSignature(currentRoot)))
            .ToList();
        foreach (var staleRoot in staleRoots)
            await _store.UpsertNodeAsync(staleRoot with { FilePath = StaleWorkspaceModuleSlice });
        if (staleRoots.Count > 0)
            paths.Add(StaleWorkspaceModuleSlice);

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Reserved slice for root workspace modules being removed; no file or package lives there.
    /// A module left on it by an interrupted run is removed by the next one.
    /// </summary>
    internal const string StaleWorkspaceModuleSlice = ".codegraph/stale-typescript-workspace-module";

    /// <summary>
    /// Adds the root workspace package modules (no directory, so no slice of their own) and their
    /// edges to an incremental write that rewrites module slices: a stale stored module sharing
    /// the root module's qualified name (for example a vendored copy of the root package) is
    /// deleted with its slice, and the root module must be written back in the same transaction.
    /// </summary>
    private static void AppendTypeScriptRootModules(
        GraphBuffer buffer,
        IEnumerable<GraphNode> analyzedNodes,
        List<GraphNode> replacementNodes,
        List<PendingEdge> replacementEdges)
    {
        var roots = WorkspaceModules(analyzedNodes)
            .Where(node => string.IsNullOrWhiteSpace(node.FilePath))
            .Select(node => node.QualifiedName)
            .ToHashSet(StringComparer.Ordinal);
        if (roots.Count == 0)
            return;

        var nodeQns = replacementNodes.Select(node => node.QualifiedName).ToHashSet(StringComparer.Ordinal);
        replacementNodes.AddRange(buffer.AllNodes.Where(node =>
            roots.Contains(node.QualifiedName) && nodeQns.Add(node.QualifiedName)));
        var edgeKeys = replacementEdges.Select(EdgeIdentity).ToHashSet(StringComparer.Ordinal);
        replacementEdges.AddRange(buffer.AllPendingEdges.Where(edge =>
            (roots.Contains(edge.SourceQN) || roots.Contains(edge.TargetQN)) && edgeKeys.Add(EdgeIdentity(edge))));

        static string EdgeIdentity(PendingEdge edge) => $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}";
    }

    private static IEnumerable<GraphNode> WorkspaceModules(IEnumerable<GraphNode> nodes) =>
        nodes.Where(node => node.Label == NodeLabel.Module
                            && TypeScriptNodeText(node.Properties.GetValueOrDefault("module_kind"))
                                == TypeScriptWorkspaceModuleKind);

    /// <summary>
    /// Stored TypeScript file slices under a path discovery excludes. Earlier sidecars analyzed
    /// copies under agent worktrees and dependency directories; those slices have no file hash
    /// row, so deletion inference never reaches them.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindExcludedTypeScriptSlicesAsync(
        string repositoryName, string rootPath, CancellationToken ct)
    {
        var excludedPath = CreateDiscoveryExclusion(rootPath);
        var excluded = (await _store.GetNodeFilePathsAsync(repositoryName, ct))
            .Select(NormalizeRelativePath)
            .Where(path => IsTypeScriptPath(path) && excludedPath(path))
            // Workspace package modules keyed by an excluded directory go too, also when no
            // analysis ran this time (no admitted tsconfig, or the sidecar was unavailable).
            .Concat(WorkspaceModules(await _store.FindNodesByLabelAsync(repositoryName, NodeLabel.Module, int.MaxValue))
                .Where(node => !string.IsNullOrWhiteSpace(node.FilePath) && excludedPath(node.FilePath!))
                .Select(node => NormalizeRelativePath(node.FilePath!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (excluded.Count > 0)
        {
            _logger.LogInformation(
                "Removing {Count} TypeScript file slice(s) of {Repository} under paths discovery excludes",
                excluded.Count, repositoryName);
        }

        return excluded;
    }

    /// <summary>
    /// Stub nodes of <paramref name="repositoryName"/> that TypeScript edges can create. Passed as
    /// orphan candidates when TypeScript slices are rewritten wholesale (excluded slices removed,
    /// or an extraction version bump re-extracted them), so stubs whose last edge went away (for
    /// example ones named after a checkout-specific <c>import("/abs/path")</c> type) are deleted.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindTypeScriptStubCandidatesAsync(string repositoryName)
    {
        var candidates = new List<string>();
        foreach (var label in new[] { NodeLabel.Interface, NodeLabel.Class, NodeLabel.Route })
        {
            candidates.AddRange((await _store.FindNodesByLabelAsync(repositoryName, label, int.MaxValue))
                .Where(node => string.IsNullOrEmpty(node.FilePath)
                               && TypeScriptNodeText(node.Properties.GetValueOrDefault("stub")) == "true")
                .Select(node => node.QualifiedName));
        }

        return candidates;
    }

    /// <summary>
    /// True for a repo-relative path discovery never yields: a configured skip pattern matches it,
    /// one of its directories is a skipped dependency directory, or, in a git working copy, git
    /// lists neither the file nor anything under the directory (ignored, or inside a submodule).
    /// </summary>
    private Func<string, bool> CreateDiscoveryExclusion(string rootPath)
    {
        var matcher = CreateDiscoveryMatcher("**/*");
        var root = Path.GetFullPath(rootPath);
        var gitFiles = GitWorkingTreeFiles.TryList(root, _logger);
        var tree = RepositoryTree.FromFileSystem(root, _fileSystem, gitFiles);
        return path =>
        {
            var relative = NormalizeRelativePath(path);
            return !matcher.Match(relative).HasMatches
                   || (gitFiles is not null && !gitFiles.Contains(relative))
                   || IsDependencyPath(relative, tree);
        };
    }

    private static bool IsTypeScriptPath(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase);

    private static string? TypeScriptNodeText(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
        JsonElement { ValueKind: JsonValueKind.True } => "true",
        JsonElement { ValueKind: JsonValueKind.False } => "false",
        JsonElement json => json.ToString(),
        bool flag => flag ? "true" : "false",
        _ => value.ToString()
    };

    private static GraphSliceRetractions? WithOrphanCandidates(
        GraphSliceRetractions retractions, IReadOnlyList<string> orphanCandidates)
    {
        if (orphanCandidates.Count > 0)
        {
            retractions = retractions with
            {
                OrphanCandidateQNs = retractions.OrphanCandidateQNs.Concat(orphanCandidates)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            };
        }

        return retractions.IsEmpty ? null : retractions;
    }
}
