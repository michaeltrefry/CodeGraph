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
        {
            var pattern=arguments.TryGetProperty("name_pattern",out var patternArg)?patternArg.GetString()??"%":"%";
            var label=arguments.TryGetProperty("label",out var labelArg)&&Enum.TryParse<CodeGraph.Models.NodeLabel>(labelArg.GetString(),true,out var parsed)?parsed:(CodeGraph.Models.NodeLabel?)null;
            var limit=arguments.TryGetProperty("limit",out var limitArg)?Math.Clamp(limitArg.GetInt32(),1,50):20;
            var results=await graph.SearchNodesAsync(repository,pattern,label,limit:limit);
            return new {untrusted_data=new {repository,items=results.Select(node=>new {id=node.Id,name=node.Name,label=node.Label.ToString(),repository=node.Repository,qualified_name=node.QualifiedName,path=node.FilePath,start_line=node.StartLine,end_line=node.EndLine}),more=results.Count==limit}};
        }
        if(tool!="dream_read_source"||KnowledgePaths.Normalize(path??"") is not {} normalized)
            throw WorkException.Invalid("Read source requires a relative path or node_id.");
        return await assistant.ReadDreamSourceAsync(repository,normalized,nodeId,ct);
    }
}
