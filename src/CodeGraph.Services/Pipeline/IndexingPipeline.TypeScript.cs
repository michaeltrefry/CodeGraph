using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;

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
        DiscoverPaths(rootPath, "**/*.ts", "**/*.tsx", $"**/{TypeScriptConfigFileName}", "**/package.json")
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The tsconfig.json whose compiler options the sidecar starts from: the root one, else the
    /// shallowest one discovery admits. The sidecar scans sources from the scope, not from it.
    /// </summary>
    private static string? SelectTypeScriptConfig(IReadOnlyList<string> scope) =>
        scope.Where(path => Path.GetFileName(path) == TypeScriptConfigFileName)
            .OrderBy(path => path.Count(ch => ch == '/'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// <paramref name="result"/> without anything discovery excludes: nodes whose file (or, for
    /// workspace package modules, directory) is excluded, edges and unresolved references
    /// touching them or an excluded file, and excluded processed files. Guards the graph against
    /// a sidecar that reads more than it was given.
    /// </summary>
    private ExtractionResult ClampToDiscovery(ExtractionResult result, string projectName, string rootPath)
    {
        var excludedPath = CreateDiscoveryExclusion(rootPath);
        bool Admitted(string? filePath) => string.IsNullOrWhiteSpace(filePath) || !excludedPath(filePath);

        // A copy can share its qualified name with the admitted original (a vendored package),
        // so nodes are dropped individually and edges only for names no admitted node keeps.
        var kept = result.Nodes.Where(node => Admitted(node.FilePath)).ToList();
        var keptQns = kept.Select(node => node.QualifiedName).ToHashSet(StringComparer.Ordinal);
        var dropped = result.Nodes
            .Select(node => node.QualifiedName)
            .Where(qualifiedName => !keptQns.Contains(qualifiedName))
            .ToHashSet(StringComparer.Ordinal);
        var filePrefix = $"{projectName}:";
        bool AdmittedFileQn(string qualifiedName) =>
            !qualifiedName.StartsWith(filePrefix, StringComparison.Ordinal)
            || Admitted(qualifiedName[filePrefix.Length..]);
        var clamped = result with
        {
            Nodes = kept,
            Edges = result.Edges
                .Where(edge => !dropped.Contains(edge.SourceQN) && !dropped.Contains(edge.TargetQN)
                               && AdmittedFileQn(edge.SourceQN) && AdmittedFileQn(edge.TargetQN))
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
            "TypeScript analysis of {Project} returned {Nodes} node(s), {Edges} edge(s) and {Files} file(s) under paths discovery excludes; dropping them",
            projectName, removedNodes, removedEdges, removedFiles);
        return clamped;
    }

    /// <summary>
    /// Paths of the TypeScript workspace package slices to replace on an incremental run: a
    /// workspace package <c>Module</c> node is keyed by its package directory, which is never a
    /// hash-tracked file, so a package that appeared, moved or disappeared is written or removed
    /// here. Unchanged packages are left alone.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindChangedTypeScriptModuleSlicesAsync(
        string projectName, IEnumerable<GraphNode> analyzedNodes)
    {
        static IEnumerable<(string QualifiedName, string FilePath)> Modules(IEnumerable<GraphNode> nodes) =>
            WorkspaceModules(nodes)
                .Where(node => !string.IsNullOrWhiteSpace(node.FilePath))
                .Select(node => (node.QualifiedName, NormalizeRelativePath(node.FilePath!)));

        var analyzed = analyzedNodes.ToList();
        var current = Modules(analyzed).ToHashSet();
        var storedNodes = await _store.FindNodesByLabelAsync(projectName, NodeLabel.Module, int.MaxValue);
        var stored = Modules(storedNodes).ToHashSet();
        var paths = current.Except(stored).Concat(stored.Except(current))
            .Select(module => module.FilePath)
            .ToList();

        // A root module (no directory) the analysis no longer yields has no slice to replace: it is
        // moved onto a reserved slice that this write then replaces with nothing.
        var currentRoots = WorkspaceModules(analyzed)
            .Where(node => string.IsNullOrWhiteSpace(node.FilePath))
            .Select(node => node.QualifiedName)
            .ToHashSet(StringComparer.Ordinal);
        var staleRoots = WorkspaceModules(storedNodes)
            .Where(node => string.IsNullOrWhiteSpace(node.FilePath) && !currentRoots.Contains(node.QualifiedName))
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
        string projectName, string rootPath, CancellationToken ct)
    {
        var excludedPath = CreateDiscoveryExclusion(rootPath);
        var excluded = (await _store.GetNodeFilePathsAsync(projectName, ct))
            .Select(NormalizeRelativePath)
            .Where(path => IsTypeScriptPath(path) && excludedPath(path))
            // Workspace package modules keyed by an excluded directory go too, also when no
            // analysis ran this time (no admitted tsconfig, or the sidecar was unavailable).
            .Concat(WorkspaceModules(await _store.FindNodesByLabelAsync(projectName, NodeLabel.Module, int.MaxValue))
                .Where(node => !string.IsNullOrWhiteSpace(node.FilePath) && excludedPath(node.FilePath!))
                .Select(node => NormalizeRelativePath(node.FilePath!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (excluded.Count > 0)
        {
            _logger.LogInformation(
                "Removing {Count} TypeScript file slice(s) of {Project} under paths discovery excludes",
                excluded.Count, projectName);
        }

        return excluded;
    }

    /// <summary>
    /// Stub nodes of <paramref name="projectName"/> that TypeScript edges can create. Passed as
    /// orphan candidates when TypeScript slices are rewritten wholesale (excluded slices removed,
    /// or an extraction version bump re-extracted them), so stubs whose last edge went away (for
    /// example ones named after a checkout-specific <c>import("/abs/path")</c> type) are deleted.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindTypeScriptStubCandidatesAsync(string projectName)
    {
        var candidates = new List<string>();
        foreach (var label in new[] { NodeLabel.Interface, NodeLabel.Class, NodeLabel.Route })
        {
            candidates.AddRange((await _store.FindNodesByLabelAsync(projectName, label, int.MaxValue))
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
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

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
