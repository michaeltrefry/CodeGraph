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
        if(arguments.ValueKind == JsonValueKind.Object && arguments.EnumerateObject().Any(p => p.Name is "project" or "project_id" or "run_id" or "session_id" or "actor"))
            throw new WorkException(WorkErrorCodes.Forbidden,"Source tools use the server run identity; it cannot be overridden. Nothing was changed.");
        var search=tool=="dream_search_graph"?WorkJson.Parse<DreamModelContract.SearchInput>(arguments,tool):null;
        var source=tool=="dream_read_source"?WorkJson.Parse<DreamModelContract.SourceInput>(arguments,tool):null;
        if(source is not null && source.Detail is not ("auto" or "full"))throw WorkException.Invalid("Source detail must be auto or full.");
        if(search is null&&source is null)throw new WorkException(WorkErrorCodes.Forbidden,"Unknown dream source tool.");
        var project=await work.GetProjectAsync(projectId,ct)??throw WorkException.NotFound("No such project.");
        var repository=search?.Repository??source?.Repository;
        var path=source?.Path;
        long? nodeId=source?.NodeId;
        if(nodeId is <=0)throw WorkException.Invalid("node_id must be a positive graph node ID.");
        CodeGraph.Models.GraphNode? graphNode=null;
        if(nodeId is {} id)
        {
            var node=await graph.FindNodeByIdAsync(id)??throw WorkException.NotFound("Graph node not found.");
            graphNode=node;
            if(repository is not null&&!repository.Equals(node.Repository,StringComparison.OrdinalIgnoreCase)) throw new WorkException(WorkErrorCodes.Forbidden,"Graph node belongs to another repository.");
            repository=node.Repository;path=node.FilePath;
        }
        if(string.IsNullOrWhiteSpace(repository)||!project.Repositories.Any(m=>m.Repository.Equals(repository,StringComparison.OrdinalIgnoreCase)))
            throw new WorkException(WorkErrorCodes.Forbidden,"The dreamer may read only the project's repositories.");
        if(tool=="dream_read_source"&&graphNode is {} metadata&&string.IsNullOrWhiteSpace(path))
        {
            var fingerprint=DreamGraphIdentity.Fingerprint(metadata.Id,metadata.Repository,metadata.Label.ToString(),metadata.Name,metadata.QualifiedName,metadata.FilePath??"",metadata.StartLine,metadata.EndLine,JsonSerializer.Serialize(metadata.Properties));
            scope.MarkGraphRead(metadata.Id,fingerprint);
            return new {untrusted_data=new {node=metadata,graph_fingerprint=fingerprint}};
        }
        if(tool=="dream_search_graph")
        {
            var pattern=search!.NamePattern;
            CodeGraph.Models.NodeLabel? label=null;
            if(search.Label is {} labelText)
                label=Enum.GetNames<CodeGraph.Models.NodeLabel>().Contains(labelText,StringComparer.Ordinal)?Enum.Parse<CodeGraph.Models.NodeLabel>(labelText):throw WorkException.Invalid("label must be one of: "+string.Join(", ",Enum.GetNames<CodeGraph.Models.NodeLabel>()));
            var limit=search.Limit;
            if(limit is <1 or >50)throw WorkException.Invalid("limit must be 1 to 50.");
            var results=await graph.SearchNodesAsync(repository,pattern,label,limit:limit);
            return new {untrusted_data=new {repository,items=results.Select(node=>new {id=node.Id,name=node.Name,label=node.Label.ToString(),repository=node.Repository,qualified_name=node.QualifiedName,path=node.FilePath,start_line=node.StartLine,end_line=node.EndLine}),more=results.Count==limit}};
        }
        if(tool!="dream_read_source"||KnowledgePaths.Normalize(path??"") is not {} normalized)
            throw WorkException.Invalid("Read source requires a relative path or node_id.");
        return await assistant.ReadDreamSourceAsync(repository,normalized,nodeId,source!.Detail=="full",ct);
    }
}
