using CodeGraph.Data;
using Microsoft.Extensions.Options;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extensions;
using CodeGraph.Services.Overlays;

namespace CodeGraph.Services.Query;

public class NodeQueryService(
    IGraphStore store,
    IOptions<RepositorySourceOptions> sourceOptionsAccessor,
    ICommitScopeResolver? commitScopes = null) : INodeQueryService
{
    private readonly ICommitScopeResolver commitScopes =
        commitScopes ?? new CommitScopeResolver(store, sourceOptionsAccessor);
    private static readonly Dictionary<string, string> ExtensionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".ts"] = "typescript",
        [".js"] = "javascript",
        [".sql"] = "sql",
        [".json"] = "json",
        [".xml"] = "xml",
        [".html"] = "html",
        [".css"] = "css",
        [".scss"] = "scss",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".cfm"] = "xml",
        [".cfc"] = "xml",
        [".swift"] = "swift",
        [".rs"] = "rust",
        [".py"] = "python",
    };
    public async Task<NodeDetailResponse?> GetDetailAsync(long id, CommitScope? scope = null)
    {
        scope ??= CommitScope.DefaultBranch(store, null);
        var graph = scope.Graph;
        var node = await graph.FindNodeByIdAsync(id);
        if (node is null)
            return null;

        // At a commit, a default-branch id hidden by the overlay resolves to the head node.
        id = node.Id;
        var outboundEdges = await graph.FindEdgesBySourceAsync(id);
        var inboundEdges = await graph.FindEdgesByTargetAsync(id);

        var crossRepoEdges = await graph.FindCrossRepoEdgesAsync(node.Repository);
        var relevantCrossRepo = crossRepoEdges
            .Where(e => e.SourceNodeId == id || e.TargetNodeId == id)
            .ToList();

        var neighborIds = outboundEdges.Select(e => e.TargetId)
            .Concat(inboundEdges.Select(e => e.SourceId))
            .Concat(relevantCrossRepo.Select(e => e.SourceNodeId == id ? e.TargetNodeId : e.SourceNodeId))
            .Distinct()
            .ToList();

        var neighbors = neighborIds.Count > 0
            ? await graph.FindNodesByIdBatchAsync(neighborIds)
            : new Dictionary<long, GraphNode>();

        // Overlay nodes (negative ids) have no stored analysis.
        var analysis = id > 0 ? await store.GetNodeAnalysisAsync(id) : null;
        if (analysis is not null)
            node = node with { Description = analysis.Description, AnalysisConfidence = analysis.Confidence };

        var outbound = outboundEdges.Select(e => MapEdge(e, e.TargetId, neighbors, node.Repository)).ToList();
        var inbound = inboundEdges.Select(e => MapEdge(e, e.SourceId, neighbors, node.Repository)).ToList();
        var crossRepo = relevantCrossRepo.Select(e =>
        {
            var neighborId = e.SourceNodeId == id ? e.TargetNodeId : e.SourceNodeId;
            var neighbor = neighbors.GetValueOrDefault(neighborId);
            var direction = e.SourceNodeId == id ? "outbound" : "inbound";
            return new CrossRepoEdgeSummary(
                e.Id,
                e.Type.ToString(),
                direction,
                e.SourceRepository,
                e.TargetRepository,
                neighborId,
                neighbor?.Name,
                neighbor?.QualifiedName,
                neighbor?.Label.ToString(),
                e.Properties);
        }).ToList();

        var commit = await commitScopes.DescribeAsync(scope, [node.Repository]);
        return new NodeDetailResponse(node, outbound, inbound, crossRepo) { CommitSha = commit.CommitSha };
    }

    public async Task<NodeListResponse> SearchAsync(
        string query,
        string? repository,
        string? label,
        int page,
        int pageSize,
        CommitScope? scope = null)
    {
        scope ??= CommitScope.DefaultBranch(store, repository);
        if (scope.IsPinned)
            repository ??= scope.Repository;
        var parsedLabel = label.TryParseEnum<NodeLabel>();

        var pattern = query.HasValue() ? query : "%";
        var nodes = await scope.Graph.SearchNodesAsync(repository, pattern, parsedLabel,
            limit: pageSize, offset: (page - 1) * pageSize);

        var total = nodes.Count < pageSize
            ? (page - 1) * pageSize + nodes.Count
            : -1;

        var commit = await commitScopes.DescribeAsync(scope, nodes.Select(node => node.Repository).Append(repository));
        return new NodeListResponse(nodes, total, page, pageSize) { CommitSha = commit.CommitSha };
    }

    public async Task<NodeSourceResponse?> GetNodeSourceAsync(long id, CommitScope? scope = null)
    {
        scope ??= CommitScope.DefaultBranch(store, null);
        var node = await scope.Graph.FindNodeByIdAsync(id);
        if (node is null || string.IsNullOrWhiteSpace(node.FilePath))
            return null;

        var source = await commitScopes.ReadSourceAsync(scope, node.Repository, node.FilePath);
        if (source.Status != CommitSourceStatus.Found)
            return null;

        var ext = Path.GetExtension(node.FilePath);
        var language = ExtensionToLanguage.GetValueOrDefault(ext, "plaintext");

        var commit = await commitScopes.DescribeAsync(scope, [node.Repository]);
        return new NodeSourceResponse(node.FilePath, node.StartLine, node.EndLine, source.Content!, language)
        {
            CommitSha = commit.CommitSha
        };
    }

    public async Task<NodeByFileResponse?> FindNodeByFileAsync(
        string repository,
        string filePath,
        int? line = null,
        CommitScope? scope = null)
    {
        scope ??= CommitScope.DefaultBranch(store, repository);
        if (scope.Overlay is { } overlay && overlay.IsOverlayRepository(repository) && overlay.IsRemovedPath(filePath))
            return null;

        var nodeId = await FindNodeIdByFileAsync(scope.Graph, repository, filePath, line);
        if (nodeId is null)
            return null;

        var commit = await commitScopes.DescribeAsync(scope, [repository]);
        return new NodeByFileResponse(nodeId.Value, commit.CommitSha);
    }

    private static async Task<long?> FindNodeIdByFileAsync(IGraphReader graph, string repository, string filePath, int? line)
    {
        // Normalize path separators — DB may store either / or \
        var forwardPath = filePath.Replace('\\', '/');
        var backPath = filePath.Replace('/', '\\');

        // Try both path separator styles
        var candidates = await graph.SearchNodesAsync(repository, "%", filePattern: forwardPath, limit: 200);
        if (candidates.Count == 0)
            candidates = await graph.SearchNodesAsync(repository, "%", filePattern: backPath, limit: 200);
        if (candidates.Count == 0)
        {
            // Try just the filename as last resort
            var fileName = Path.GetFileName(filePath);
            candidates = await graph.SearchNodesAsync(repository, "%", filePattern: fileName, limit: 200);
        }

        if (candidates.Count == 0)
            return null;

        if (line is not null)
        {
            // Find the smallest node (by line span) that contains the target line
            var containing = candidates
                .Where(n => n.StartLine > 0 && n.EndLine > 0 && n.StartLine <= line && n.EndLine >= line)
                .OrderBy(n => n.EndLine - n.StartLine)
                .ThenByDescending(n => n.Label == NodeLabel.Method ? 0 : n.Label == NodeLabel.Class ? 1 : 2)
                .FirstOrDefault();

            if (containing is not null)
                return containing.Id;
        }

        // Fall back to File node, or first node in the file
        var fileNode = candidates.FirstOrDefault(n => n.Label == NodeLabel.File);
        return fileNode?.Id ?? candidates.First().Id;
    }

    public async Task SetDoNotTrustAsync(long nodeId, bool doNotTrust)
    {
        await store.SetDoNotTrustAsync(nodeId, doNotTrust);
    }

    private static EdgeSummary MapEdge(
        GraphEdge edge,
        long neighborId,
        Dictionary<long, GraphNode> neighbors,
        string currentRepository)
    {
        var neighbor = neighbors.GetValueOrDefault(neighborId);
        var isCrossRepository = neighbor is not null && neighbor.Repository != currentRepository;
        return new EdgeSummary(
            edge.Id,
            edge.Type.ToString(),
            neighborId,
            neighbor?.Name,
            neighbor?.QualifiedName,
            neighbor?.Label.ToString(),
            neighbor?.Repository,
            isCrossRepository,
            edge.Properties);
    }
}
