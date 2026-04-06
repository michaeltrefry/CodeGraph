using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Models.Responses;
using TC.CodeGraphApi.Services.Configuration;
using TC.CodeGraphApi.Services.Extensions;

namespace TC.CodeGraphApi.Services.Query;

public class NodeQueryService(IGraphStore store, IFileSystem fileSystem, GitLabOptions gitLabOptions) : INodeQueryService
{
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
    };
    public async Task<NodeDetailResponse?> GetDetailAsync(long id)
    {
        var node = await store.FindNodeByIdAsync(id);
        if (node is null)
            return null;

        var outboundEdges = await store.FindEdgesBySourceAsync(id);
        var inboundEdges = await store.FindEdgesByTargetAsync(id);

        var crossRepoEdges = await store.FindCrossRepoEdgesAsync(node.Project);
        var relevantCrossRepo = crossRepoEdges
            .Where(e => e.SourceNodeId == id || e.TargetNodeId == id)
            .ToList();

        var neighborIds = outboundEdges.Select(e => e.TargetId)
            .Concat(inboundEdges.Select(e => e.SourceId))
            .Concat(relevantCrossRepo.Select(e => e.SourceNodeId == id ? e.TargetNodeId : e.SourceNodeId))
            .Distinct()
            .ToList();

        var neighbors = neighborIds.Count > 0
            ? await store.FindNodesByIdBatchAsync(neighborIds)
            : new Dictionary<long, GraphNode>();

        var analysis = await store.GetNodeAnalysisAsync(id);
        if (analysis is not null)
            node = node with { Description = analysis.Description, AnalysisConfidence = analysis.Confidence };

        var outbound = outboundEdges.Select(e => MapEdge(e, e.TargetId, neighbors, node.Project)).ToList();
        var inbound = inboundEdges.Select(e => MapEdge(e, e.SourceId, neighbors, node.Project)).ToList();
        var crossRepo = relevantCrossRepo.Select(e =>
        {
            var neighborId = e.SourceNodeId == id ? e.TargetNodeId : e.SourceNodeId;
            var neighbor = neighbors.GetValueOrDefault(neighborId);
            var direction = e.SourceNodeId == id ? "outbound" : "inbound";
            return new CrossRepoEdgeSummary(
                e.Id,
                e.Type.ToString(),
                direction,
                e.SourceProject,
                e.TargetProject,
                neighborId,
                neighbor?.Name,
                neighbor?.QualifiedName,
                neighbor?.Label.ToString(),
                e.Properties);
        }).ToList();

        return new NodeDetailResponse(node, outbound, inbound, crossRepo);
    }

    public async Task<NodeListResponse> SearchAsync(string query, string? project, string? label, int page, int pageSize)
    {
        var parsedLabel = label.TryParseEnum<NodeLabel>();

        var pattern = query.HasValue() ? query : "%";
        var nodes = await store.SearchNodesAsync(project, pattern, parsedLabel,
            limit: pageSize, offset: (page - 1) * pageSize);

        var total = nodes.Count < pageSize
            ? (page - 1) * pageSize + nodes.Count
            : -1;

        return new NodeListResponse(nodes, total, page, pageSize);
    }

    public async Task<NodeSourceResponse?> GetNodeSourceAsync(long id)
    {
        var node = await store.FindNodeByIdAsync(id);
        if (node is null || string.IsNullOrWhiteSpace(node.FilePath))
            return null;

        var fullPath = await RepoFileResolver.ResolveAsync(
            node.Project, node.FilePath, gitLabOptions, store);

        if (fullPath is null)
            return null;

        var content = await fileSystem.ReadAllTextAsync(fullPath);
        var ext = Path.GetExtension(fullPath);
        var language = ExtensionToLanguage.GetValueOrDefault(ext, "plaintext");

        return new NodeSourceResponse(node.FilePath, node.StartLine, node.EndLine, content, language);
    }

    public async Task<long?> FindNodeByFileAsync(string project, string filePath, int? line = null)
    {
        // Normalize path separators — DB may store either / or \
        var forwardPath = filePath.Replace('\\', '/');
        var backPath = filePath.Replace('/', '\\');

        // Try both path separator styles
        var candidates = await store.SearchNodesAsync(project, "%", filePattern: forwardPath, limit: 200);
        if (candidates.Count == 0)
            candidates = await store.SearchNodesAsync(project, "%", filePattern: backPath, limit: 200);
        if (candidates.Count == 0)
        {
            // Try just the filename as last resort
            var fileName = Path.GetFileName(filePath);
            candidates = await store.SearchNodesAsync(project, "%", filePattern: fileName, limit: 200);
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
        string currentProject)
    {
        var neighbor = neighbors.GetValueOrDefault(neighborId);
        var isCrossProject = neighbor is not null && neighbor.Project != currentProject;
        return new EdgeSummary(
            edge.Id,
            edge.Type.ToString(),
            neighborId,
            neighbor?.Name,
            neighbor?.QualifiedName,
            neighbor?.Label.ToString(),
            neighbor?.Project,
            isCrossProject,
            edge.Properties);
    }
}
