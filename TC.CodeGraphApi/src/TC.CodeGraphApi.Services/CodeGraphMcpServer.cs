using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services;

[McpServerToolType]
public class CodeGraphMcpServer
{
    private readonly GraphQueryEngine _query;
    private readonly IGraphStore _store;
    private readonly IndexingPipeline _pipeline;
    private readonly ILogger<CodeGraphMcpServer> _logger;

    public CodeGraphMcpServer(
        GraphQueryEngine query,
        IGraphStore store,
        IndexingPipeline pipeline,
        ILogger<CodeGraphMcpServer> logger)
    {
        _query = query;
        _store = store;
        _pipeline = pipeline;
        _logger = logger;
    }

    [McpServerTool(Name = "get_graph_schema"),
     Description("Describe the available node types, edge types, and their properties in the knowledge graph.")]
    public string GetGraphSchema()
    {
        return """
            ## Node Types
            Project, Namespace, Folder, File, Class, Interface, Enum, Struct,
            Record, Function, Method, Property, Constructor, Delegate, Route,
            Service, Table, View, StoredProcedure, Event, Queue, Exchange,
            Component, Module, Job, NuGetPackage

            ## Edge Types
            CONTAINS_FILE, CONTAINS_FOLDER, CONTAINS_NAMESPACE, DEFINES,
            DEFINES_METHOD, CALLS, IMPORTS, IMPLEMENTS, INHERITS, USES_TYPE,
            INJECTS, HTTP_CALLS, HANDLES, QUERIES, PUBLISHES, CONSUMES,
            REFERENCES_PACKAGE, RENDERS, SUBSCRIBES, FILE_CHANGES_WITH, SCHEDULES

            ## Key Properties
            - Route: http_method, route_template, handler
            - Service: lifetime, interface, implementation
            - Event: queue_name, exchange_name
            - Method: signature, return_type, is_async, complexity, is_entry_point
            - CALLS edge: confidence, confidence_band
            - HTTP_CALLS edge: url_pattern, http_method, source_repo, target_repo
            """;
    }

    [McpServerTool(Name = "list_projects"),
     Description("List all indexed repositories with metadata: last indexed date, language, whether foundational.")]
    public async Task<string> ListProjects()
    {
        var projects = await _store.ListProjectsAsync();
        if (projects.Count == 0)
            return "No projects indexed yet.";

        var lines = new List<string> { $"## Indexed Projects ({projects.Count})\n" };
        foreach (var p in projects.OrderBy(p => p.Name))
        {
            var flags = new List<string>();
            if (p.IsFoundational) flags.Add("foundational");
            if (!string.IsNullOrEmpty(p.Language)) flags.Add(p.Language);
            if (!string.IsNullOrEmpty(p.Framework)) flags.Add(p.Framework);

            var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            var indexed = p.IndexedAt != default ? $" (indexed: {p.IndexedAt:yyyy-MM-dd HH:mm})" : "";
            lines.Add($"- **{p.Name}**{flagStr}{indexed}");
        }

        return string.Join('\n', lines);
    }

    [McpServerTool(Name = "search_graph"),
     Description("Search for services, endpoints, models, events, or any code element by name pattern. Supports filtering by type and project.")]
    public async Task<string> SearchGraph(
        [Description("Name pattern to search for (supports % wildcards)")] string namePattern,
        [Description("Filter by node type: Class, Method, Route, Service, Event, Queue, Table, Interface, etc.")] string? label = null,
        [Description("Filter by project/repository name")] string? project = null,
        [Description("Max results (default 20)")] int limit = 20)
    {
        NodeLabel? parsedLabel = null;
        if (label is not null && Enum.TryParse<NodeLabel>(label, ignoreCase: true, out var l))
            parsedLabel = l;

        var result = await _query.SearchAsync(new SearchRequest(namePattern, parsedLabel, project, limit));

        if (result.Nodes.Count == 0)
            return $"No results found for '{namePattern}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Search Results ({result.TotalCount} matches)\n");

        foreach (var node in result.Nodes)
        {
            sb.AppendLine($"- **{node.Name}** ({node.Label}) — {node.Project}");
            if (!string.IsNullOrEmpty(node.FilePath))
                sb.AppendLine($"  File: {node.FilePath}:{node.StartLine}");
            if (!string.IsNullOrEmpty(node.QualifiedName) && node.QualifiedName != node.Name)
                sb.AppendLine($"  QN: {node.QualifiedName}");

            foreach (var prop in node.Properties.Where(p => p.Value is not null))
                sb.AppendLine($"  {prop.Key}: {prop.Value}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_service_summary"),
     Description("Get the natural language description of a service/repository, including what it does, its endpoints, dependencies, and what depends on it.")]
    public async Task<string> GetServiceSummary(
        [Description("Project/repository name")] string project)
    {
        var summary = await _store.GetProjectSummaryAsync(project);
        if (summary is null)
            return $"No analysis available for '{project}'. Run the analyze command first.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {project}");
        sb.AppendLine($"**Confidence:** {summary.Confidence}");
        if (summary.ModelUsed is not null)
            sb.AppendLine($"**Model:** {summary.ModelUsed}");
        sb.AppendLine($"**Last updated:** {summary.UpdatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine(summary.Summary);

        return sb.ToString();
    }

    [McpServerTool(Name = "trace_call_path"),
     Description("Trace callers or callees of a function/method. Shows the call chain across services. Use direction 'inbound' to find callers, 'outbound' to find callees, 'both' for both.")]
    public async Task<string> TraceCallPath(
        [Description("Function or method name to trace")] string functionName,
        [Description("Trace direction: inbound, outbound, or both (default: both)")] string direction = "both",
        [Description("How many levels deep to trace (default: 3)")] int depth = 3,
        [Description("Filter by project")] string? project = null)
    {
        if (!Enum.TryParse<TraceDirection>(direction, ignoreCase: true, out var dir))
            dir = TraceDirection.Both;

        var result = await _query.TraceCallPathAsync(functionName, project, dir, depth);

        if (result.Count == 0)
            return $"No call paths found for '{functionName}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Call Path: {functionName} ({direction})\n");

        foreach (var entry in result)
        {
            var indent = new string(' ', entry.Depth * 2);
            sb.AppendLine($"{indent}- **{entry.Node.Name}** ({entry.Node.Label}) — {entry.Node.Project}");
            sb.AppendLine($"{indent}  Edge: {entry.EdgeType}");
            if (!string.IsNullOrEmpty(entry.Node.FilePath))
                sb.AppendLine($"{indent}  File: {entry.Node.FilePath}:{entry.Node.StartLine}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "trace_data_lineage"),
     Description("Follow a data model from its database origin through all services that produce, transform, or consume it. Shows the complete data flow across the system.")]
    public async Task<string> TraceDataLineage(
        [Description("Model/DTO/event class name to trace")] string modelName,
        [Description("Filter by project")] string? project = null)
    {
        var result = await _query.TraceDataLineageAsync(modelName, project);

        if (result.Producers.Count == 0 && result.Consumers.Count == 0 && result.CrossRepoEdges.Count == 0)
            return $"No data lineage found for '{modelName}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Data Lineage: {modelName}\n");

        if (result.Producers.Count > 0)
        {
            sb.AppendLine("### Producers (inbound)\n");
            foreach (var entry in result.Producers)
            {
                var indent = new string(' ', entry.Depth * 2);
                sb.AppendLine($"{indent}- **{entry.Node.Name}** ({entry.Node.Label}) — {entry.Node.Project} [{entry.EdgeType}]");
            }
            sb.AppendLine();
        }

        if (result.Consumers.Count > 0)
        {
            sb.AppendLine("### Consumers (outbound)\n");
            foreach (var entry in result.Consumers)
            {
                var indent = new string(' ', entry.Depth * 2);
                sb.AppendLine($"{indent}- **{entry.Node.Name}** ({entry.Node.Label}) — {entry.Node.Project} [{entry.EdgeType}]");
            }
            sb.AppendLine();
        }

        if (result.CrossRepoEdges.Count > 0)
        {
            sb.AppendLine("### Cross-Repo References\n");
            foreach (var edge in result.CrossRepoEdges)
                sb.AppendLine($"- {edge.SourceProject} → {edge.TargetProject} ({edge.Type})");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "find_consumers"),
     Description("Find all services/methods that consume a given event, endpoint, or model. Shows cross-repo dependencies.")]
    public async Task<string> FindConsumers(
        [Description("Event, endpoint, or model name")] string name,
        [Description("Filter by project")] string? project = null)
    {
        var result = await _query.FindConsumersAsync(name, project);

        if (result.Count == 0)
            return $"No consumers found for '{name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Consumers of {name} ({result.Count})\n");

        foreach (var consumer in result)
        {
            sb.AppendLine($"- **{consumer.Name}** — {consumer.Project} ({consumer.EdgeType})");
            sb.AppendLine($"  QN: {consumer.QualifiedName}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "find_publishers"),
     Description("Find all services that publish to a given queue, exchange, or event type.")]
    public async Task<string> FindPublishers(
        [Description("Queue, exchange, or event name")] string name,
        [Description("Filter by project")] string? project = null)
    {
        var result = await _query.FindPublishersAsync(name, project);

        if (result.Count == 0)
            return $"No publishers found for '{name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Publishers to {name} ({result.Count})\n");

        foreach (var publisher in result)
        {
            sb.AppendLine($"- **{publisher.Name}** — {publisher.Project} ({publisher.EdgeType})");
            sb.AppendLine($"  QN: {publisher.QualifiedName}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_architecture"),
     Description("Get architecture overview for a project — hotspots, dependency analysis, complexity metrics.")]
    public async Task<string> GetArchitecture(
        [Description("Project name")] string project)
    {
        var report = await _query.GetArchitectureAsync(project);

        var sb = new StringBuilder();
        sb.AppendLine($"# Architecture: {report.Project}\n");

        if (report.Summary is not null)
        {
            sb.AppendLine($"**Confidence:** {report.Confidence}");
            sb.AppendLine();
            sb.AppendLine(report.Summary);
            sb.AppendLine();
        }

        sb.AppendLine("### Node Counts\n");
        foreach (var (label, count) in report.NodeCounts.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"- {label}: {count:N0}");
        sb.AppendLine();

        if (report.Hotspots.Count > 0)
        {
            sb.AppendLine("### Hotspots (high fan-in methods)\n");
            foreach (var h in report.Hotspots)
                sb.AppendLine($"- **{h.Name}** (fan-in: {h.FanIn}) — {h.Reason}");
            sb.AppendLine();
        }

        if (report.InboundDependencies.Count > 0)
        {
            sb.AppendLine("### Inbound Dependencies (other repos depend on this)\n");
            foreach (var edge in report.InboundDependencies)
                sb.AppendLine($"- {edge.SourceProject} → this ({edge.Type})");
            sb.AppendLine();
        }

        if (report.OutboundDependencies.Count > 0)
        {
            sb.AppendLine("### Outbound Dependencies (this depends on other repos)\n");
            foreach (var edge in report.OutboundDependencies)
                sb.AppendLine($"- this → {edge.TargetProject} ({edge.Type})");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "find_archival_candidates"),
     Description("Find repositories with no inbound or outbound dependencies — candidates for archival.")]
    public async Task<string> FindArchivalCandidates()
    {
        var candidates = await _query.FindArchivalCandidatesAsync();

        if (candidates.Count == 0)
            return "No archival candidates found (all projects have cross-repo dependencies).";

        var sb = new StringBuilder();
        sb.AppendLine($"## Archival Candidates ({candidates.Count})\n");
        sb.AppendLine("These projects have no cross-repo edges (no inbound or outbound dependencies):\n");

        foreach (var p in candidates.OrderBy(p => p.Name))
        {
            var indexed = p.IndexedAt != default ? $" (indexed: {p.IndexedAt:yyyy-MM-dd})" : "";
            sb.AppendLine($"- **{p.Name}**{indexed}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_code_snippet"),
     Description("Read actual source code from a repository. Use when the graph and summaries don't provide enough detail.")]
    public async Task<string> GetCodeSnippet(
        [Description("Project name")] string project,
        [Description("File path relative to repo root")] string filePath,
        [Description("Start line (0 for beginning)")] int startLine = 0,
        [Description("End line (0 for entire file)")] int endLine = 0)
    {
        var projects = await _store.ListProjectsAsync();
        var projectInfo = projects.FirstOrDefault(p =>
            string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));

        if (projectInfo?.LocalPath is null)
            return $"Project '{project}' not found or has no local path.";

        var fullPath = Path.Combine(projectInfo.LocalPath, filePath);
        if (!File.Exists(fullPath))
            return $"File not found: {filePath}";

        var lines = await File.ReadAllLinesAsync(fullPath);
        var start = startLine > 0 ? startLine - 1 : 0;
        var end = endLine > 0 ? Math.Min(endLine, lines.Length) : lines.Length;

        if (start >= lines.Length)
            return $"Start line {startLine} exceeds file length ({lines.Length} lines).";

        var snippet = lines[start..end];
        var sb = new StringBuilder();
        sb.AppendLine($"```csharp // {filePath}:{start + 1}-{end}");
        for (var i = 0; i < snippet.Length; i++)
            sb.AppendLine($"{start + i + 1,5} | {snippet[i]}");
        sb.AppendLine("```");

        return sb.ToString();
    }

    [McpServerTool(Name = "index_repository"),
     Description("Trigger indexing of a repository. Use for manual re-indexing.")]
    public async Task<string> IndexRepository(
        [Description("Absolute path to repository")] string repoPath,
        [Description("Project name (defaults to directory name)")] string? name = null)
    {
        if (!Directory.Exists(repoPath))
            return $"Directory not found: {repoPath}";

        var projectName = name ?? Path.GetFileName(repoPath);

        try
        {
            await _pipeline.IndexProjectAsync(projectName, repoPath);
            return $"Indexed {projectName} successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Index failed for {Project}", projectName);
            return $"Index failed for {projectName}: {ex.Message}";
        }
    }
}
