using Microsoft.Extensions.FileSystemGlobbing;
using CodeGraph.Models;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Branch overlay extraction: builds the graph slice for a set of changed files without
/// writing anything to the default-branch graph.
/// </summary>
public partial class IndexingPipeline
{
    /// <summary>
    /// True when <paramref name="relativePath"/> belongs to the file universe the default-branch
    /// index covers (a supported extension that no skip pattern excludes).
    /// </summary>
    public bool IsIndexablePath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var extension = Path.GetExtension(normalized);
        if (!_extractors.Any(extractor => extractor.SupportedExtensions.Contains(extension)))
            return false;

        var matcher = new Matcher();
        matcher.AddInclude("**/*");
        foreach (var skip in _options.SkipPatterns)
            matcher.AddExclude(skip);
        return matcher.Match(normalized).HasMatches;
    }

    /// <summary>
    /// Extracts the overlay slice for <paramref name="changedPaths"/> materialized under
    /// <paramref name="rootPath"/>. Calls are resolved against the overlay files first and then
    /// against the persisted default-branch graph, excluding every changed or removed path.
    /// Only nodes that belong to a changed file are returned; edges are returned when either
    /// endpoint is one of those nodes. The default-branch graph is never written.
    /// </summary>
    public async Task<BranchOverlaySlice> ExtractOverlayAsync(
        string projectName,
        string rootPath,
        IReadOnlyCollection<string> changedPaths,
        IReadOnlyCollection<string> removedPaths,
        IReadOnlyCollection<string> csprojRelativePaths,
        CancellationToken ct = default)
    {
        var buffer = new GraphBuffer();
        var normalizedChanged = changedPaths
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = normalizedChanged
            .Select(path => Path.Combine(rootPath, path))
            .Where(_fileSystem.FileExists)
            .ToList();
        var csprojFiles = csprojRelativePaths
            .Select(path => Path.Combine(rootPath, NormalizeRelativePath(path)))
            .ToArray();

        // Untrusted: overlays never run repository-controlled restore/MSBuild tooling.
        var context = new ExtractorContext
        {
            ProjectName = projectName,
            RootPath = rootPath,
            RepositoryToolingTrust = RepositoryToolingTrust.Untrusted
        };

        CreateStructuralNodes(projectName, rootPath, files, csprojFiles, buffer);
        if (files.Count > 0)
            await ExtractFilesAsync(
                files, rootPath, context, buffer, failOnExtractionError: false, rustSourceOwners: [], ct);

        ResolveImports(buffer);
        await ResolveCallsAsync(
            projectName,
            buffer,
            includePersistedNodes: true,
            excludedPersistedFiles: normalizedChanged
                .Concat(removedPaths.Select(NormalizeRelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            ct);

        var nodes = buffer.AllNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.FilePath)
                           && normalizedChanged.Contains(NormalizeRelativePath(node.FilePath)))
            .ToList();
        var nodeQns = nodes.Select(node => node.QualifiedName).ToHashSet(StringComparer.Ordinal);
        var edges = buffer.AllPendingEdges
            .Where(edge => nodeQns.Contains(edge.SourceQN) || nodeQns.Contains(edge.TargetQN))
            .Distinct()
            .ToList();

        return new BranchOverlaySlice(nodes, edges);
    }
}

public sealed record BranchOverlaySlice(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<PendingEdge> Edges);
