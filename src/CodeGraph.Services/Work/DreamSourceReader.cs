using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Assistant;

namespace CodeGraph.Services.Work;

public sealed class DreamSourceReader(GraphAssistant assistant,IGraphStore graph,IWorkStore work) : IDreamSourceReader
{
    public async Task<object> ExecuteAsync(Guid projectId,string tool,JsonElement arguments,CancellationToken ct=default)
    {
        var scope=DreamExecutionScope.Current??throw WorkException.Invalid("A source read requires a dreamer run.");
        DreamPolicy.RequireProject(scope.Actor,projectId);
        var project=await work.GetProjectAsync(projectId,ct)??throw WorkException.NotFound("No such project.");
        var repository=arguments.TryGetProperty("repository",out var r)?r.GetString():null;
        var path=arguments.TryGetProperty("path",out var p)?p.GetString():null;
        long? nodeId=arguments.TryGetProperty("node_id",out var n)?n.GetInt64():null;
        if(nodeId is {} id)
        {
            var node=await graph.FindNodeByIdAsync(id)??throw WorkException.NotFound("Graph node not found.");
            if(repository is not null&&!repository.Equals(node.Repository,StringComparison.OrdinalIgnoreCase)) throw WorkException.Invalid("Graph node belongs to another repository.");
            repository=node.Repository;path=node.FilePath;
        }
        if(string.IsNullOrWhiteSpace(repository)||!project.Repositories.Any(m=>m.Repository.Equals(repository,StringComparison.OrdinalIgnoreCase)))
            throw WorkException.Invalid("The dreamer may read only the project's repositories.");
        if(tool=="dream_search_graph")
            return new {untrusted_data=await assistant.SearchDreamGraphAsync(repository,
                arguments.TryGetProperty("name_pattern",out var pattern)?pattern.GetString()??"%":"%",
                arguments.TryGetProperty("label",out var label)?label.GetString():null,
                arguments.TryGetProperty("limit",out var limit)?Math.Clamp(limit.GetInt32(),1,50):20)};
        if(tool!="dream_read_source"||KnowledgePaths.Normalize(path??"") is not {} normalized)
            throw WorkException.Invalid("Read source requires a relative path or node_id.");
        return await assistant.ReadDreamSourceAsync(repository,normalized,nodeId,ct);
    }
}
