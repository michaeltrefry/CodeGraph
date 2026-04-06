using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services.Query;

public class GraphOverviewService(IGraphStore store) : IGraphOverviewService
{
    public async Task<GraphOverviewResponse> GetOverviewAsync()
    {
        var repos = await store.ListRepositoriesAsync();
        var allEdges = await store.GetAllCrossRepoEdgesAsync();

        var aggregated = allEdges
            .GroupBy(e => (Source: e.SourceProject, Target: e.TargetProject))
            .Select(g => new GraphOverviewEdge(
                g.Key.Source,
                g.Key.Target,
                g.Count(),
                g.GroupBy(e => e.Type.ToString())
                 .ToDictionary(tg => tg.Key, tg => tg.Count())))
            .ToList();

        var nodes = repos.Select(r => new GraphOverviewNode(
            r.Name,
            r.GitLabGroup,
            r.Language,
            r.Framework,
            r.IsFoundational)).ToList();

        return new GraphOverviewResponse(nodes, aggregated);
    }
}
