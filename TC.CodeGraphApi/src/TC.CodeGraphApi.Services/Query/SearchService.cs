using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services.Query;

public class SearchService(IGraphStore store) : ISearchService
{
    // Node labels worth surfacing in search (skip structural containers like Folder/File/Namespace)
    private static readonly HashSet<NodeLabel> SearchableLabels =
    [
        NodeLabel.Class, NodeLabel.Interface, NodeLabel.Enum, NodeLabel.Struct, NodeLabel.Record,
        NodeLabel.Method, NodeLabel.Route, NodeLabel.Service, NodeLabel.Event,
        NodeLabel.Queue, NodeLabel.Exchange, NodeLabel.Job, NodeLabel.Table,
        NodeLabel.View, NodeLabel.StoredProcedure, NodeLabel.Component, NodeLabel.DotnetProject
    ];

    public async Task<UnifiedSearchResponse> SearchAsync(string query, int page = 1, int pageSize = 25)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new UnifiedSearchResponse([], 0, page, pageSize);

        // Search repos and nodes in parallel — both filtered at the store layer
        var reposTask = store.SearchRepositoriesAsync(search: query, pageSize: 50);
        var nodesTask = store.SearchNodesAsync(
            project: null,
            namePattern: query,
            limit: 200,
            offset: 0);

        await Task.WhenAll(reposTask, nodesTask);

        var repoResult = await reposTask;
        var nodes = await nodesTask;

        var results = new List<SearchResultItem>();

        // Map matching repositories
        foreach (var repo in repoResult.Items)
        {
            var desc = repo.Framework is not null
                ? $"{repo.Language} / {repo.Framework}"
                : repo.Language;
            if (repo.IsFoundational)
                desc = $"[Foundational] {desc}";

            results.Add(new SearchResultItem(
                Type: "repository",
                Name: repo.Name,
                Description: desc,
                NodeLabel: null,
                Project: repo.Name,
                NodeId: null,
                QualifiedName: null));
        }

        // Match nodes (filter to searchable labels)
        foreach (var node in nodes)
        {
            if (!SearchableLabels.Contains(node.Label))
                continue;

            var desc = node.Description;
            if (string.IsNullOrWhiteSpace(desc))
                desc = node.QualifiedName;

            results.Add(new SearchResultItem(
                Type: "node",
                Name: node.Name,
                Description: desc,
                NodeLabel: node.Label.ToString(),
                Project: node.Project,
                NodeId: node.Id,
                QualifiedName: node.QualifiedName));
        }

        // Sort: exact name matches first, then repos before nodes, then alphabetical
        results = results
            .OrderByDescending(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Type == "repository" ? 0 : 1)
            .ThenBy(r => r.Name)
            .ToList();

        var total = results.Count;
        var paged = results
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new UnifiedSearchResponse(paged, total, page, pageSize);
    }
}
