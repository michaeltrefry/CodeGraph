using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;
using CodeGraph.Services.Extensions;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.WikiRag;

namespace CodeGraph.Services.Assistant;

[McpServerToolType]
public class CodeGraphMcpServer(
    GraphQueryEngine query,
    IProjectQueryService projectQuery,
    IGraphStore store,
    IWikiStore wikiStore,
    IOptions<RepositorySourceOptions> sourceOptionsAccessor,
    ICommunityDetectionService communityDetection,
    IImpactAnalysisService impactAnalysis,
    IConventionEmbeddingService? conventionEmbeddingService = null,
    ICommitScopeResolver? commitScopes = null)
{
    private const string ShaDescription =
        "Optional full 40-hex commit SHA. Answers from the ready branch overlay for that commit " +
        "(its changed files over the default-branch index); a SHA without a ready overlay returns a " +
        "typed not-indexed result. Omit for the default branch.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly ICommitScopeResolver commitScopes =
        commitScopes ?? new CommitScopeResolver(store, sourceOptionsAccessor);

    /// <summary>
    /// Resolves the graph for <paramref name="sha"/> and runs <paramref name="answer"/> against it.
    /// The answer returns its text and the projects it is about; the result names the commit.
    /// </summary>
    private async Task<CallToolResult> AnswerAsync(
        string? project,
        string? sha,
        Func<CommitScope, string?, Task<(string Text, IEnumerable<string?> Projects)>> answer)
    {
        var scope = await commitScopes.ResolveAsync(project, sha);
        if (scope.NotIndexed is { } notIndexed)
            return CommitAnswer.NotIndexed(notIndexed);

        var effectiveProject = scope.IsPinned ? scope.Project : project;
        var (text, projects) = await answer(scope, effectiveProject);
        var commit = await commitScopes.DescribeAsync(scope, projects.Append(effectiveProject));
        return CommitAnswer.Create(text, commit, scope);
    }

    /// <summary>
    /// An answer that lists graph nodes, with how many of them are low-confidence, and (for an
    /// overlay answer) how many of the overlay's unmapped default-branch edges it would have reached.
    /// </summary>
    private sealed record NodeAnswer(
        string Text,
        IEnumerable<string?> Projects,
        int LowConfidenceCount,
        bool SummaryInText = false,
        Func<OverlayGraphView, int>? CountUnmapped = null);

    /// <summary>
    /// <see cref="AnswerAsync"/> for answers that list graph nodes: adds the answer-level
    /// low-confidence line (unless the text already carries it) and
    /// <c>structuredContent.lowConfidence</c>/<c>lowConfidenceCount</c>. With
    /// <paramref name="reportsUnmapped"/> (impact and trace answers), an overlay answer also reports
    /// the default-branch edges the overlay could not map that are relevant to this answer
    /// (<see cref="NodeAnswer.CountUnmapped"/>, <c>unmappedInboundEdges</c>).
    /// </summary>
    private async Task<CallToolResult> NodeAnswerAsync(
        string? project,
        string? sha,
        bool reportsUnmapped,
        Func<CommitScope, string?, Task<NodeAnswer>> answer)
    {
        var scope = await commitScopes.ResolveAsync(project, sha);
        if (scope.NotIndexed is { } notIndexed)
            return CommitAnswer.NotIndexed(notIndexed);

        var effectiveProject = scope.IsPinned ? scope.Project : project;
        var result = await answer(scope, effectiveProject);
        var notices = new List<string>();
        if (!result.SummaryInText && AnswerConfidence.SummaryLine(result.LowConfidenceCount) is { } lowConfidence)
            notices.Add(lowConfidence);

        var extra = new JsonObject
        {
            ["lowConfidence"] = result.LowConfidenceCount > 0,
            ["lowConfidenceCount"] = result.LowConfidenceCount
        };
        if (reportsUnmapped && scope.Overlay is { } overlay)
        {
            var unmappedCount = result.CountUnmapped?.Invoke(overlay) ?? 0;
            extra["unmappedInboundEdges"] = unmappedCount;
            if (AnswerConfidence.UnmappedLine(unmappedCount) is { } unmapped)
                notices.Add(unmapped);
        }

        var text = notices.Count == 0
            ? result.Text
            : result.Text.TrimEnd() + "\n\n" + string.Join("\n", notices) + "\n";
        var commit = await commitScopes.DescribeAsync(scope, result.Projects.Append(effectiveProject));
        return CommitAnswer.Create(text, commit, scope, extra);
    }

    [McpServerTool(Name = "get_graph_schema"),
     Description("Describe the available node types, edge types, and their properties in the knowledge graph.")]
    public string GetGraphSchema()
    {
        return """
            ## Node Types
            Repository, DotnetProject, Namespace, Folder, File, Class, Interface,
            Enum, Struct, Record, Function, Method, Property, Constructor, Delegate,
            Route, Service, Table, View, StoredProcedure, Event, Queue, Exchange,
            Component, Module, Job, NuGetPackage, Package, ExternalSymbol

            ## Edge Types
            CONTAINS_FILE, CONTAINS_FOLDER, CONTAINS_NAMESPACE, CONTAINS_PROJECT,
            DEFINES, DEFINES_METHOD, CALLS, IMPORTS, IMPLEMENTS, INHERITS,
            USES_TYPE, INJECTS, HTTP_CALLS, HANDLES, QUERIES, PUBLISHES, CONSUMES,
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

    [Obsolete("Use search_projects instead.")]
    [McpServerTool(Name = "list_projects"),
     Description("Obsolete. Use search_projects instead. Lists all indexed repositories with their git repository URL and the commit of their default-branch index.")]
    public async Task<string> ListProjects()
    {
        var projects = await store.ListRepositoriesAsync();
        if (projects.Count == 0)
            return "No projects indexed yet.";

        return await FormatProjectsAsync(projects.OrderBy(p => p.Name).ToList(), projects.Count);
    }

    [McpServerTool(Name = "search_projects"),
     Description("Search indexed repositories by partial or wildcard name with metadata including git repository URL and the commit of the default-branch index. Returns the same markdown list shape as list_projects, with Repo and Last commit lines for each project.")]
    public async Task<string> SearchProjects(
        [Description("Optional partial or wildcard repository name search. Supports plain substrings plus * and % wildcards.")] string? search = null,
        [Description("Optional source group filter")] string? group = null,
        [Description("Page number, 1-based")] int page = 1,
        [Description("Page size")] int pageSize = 25)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var result = await store.SearchRepositoriesAsync(search, group, page, pageSize);
        if (result.TotalCount == 0)
            return string.IsNullOrWhiteSpace(search) && string.IsNullOrWhiteSpace(group)
                ? "No projects indexed yet."
                : "No projects matched the search.";

        return await FormatProjectsAsync(result.Items, result.TotalCount);
    }

    private async Task<string> FormatProjectsAsync(IReadOnlyList<ProjectInfo> projects, int totalCount)
    {
        // The default-branch index commit: sync state first (what indexing recorded), then the repository row.
        var syncStates = await store.GetSyncStatesAsync(projects.Select(p => p.Name).ToList());
        var lines = new List<string> { $"## Indexed Projects ({totalCount})\n" };
        foreach (var p in projects)
        {
            var flags = new List<string>();
            if (p.IsFoundational) flags.Add("foundational");
            if (!string.IsNullOrEmpty(p.Language)) flags.Add(p.Language);
            if (!string.IsNullOrEmpty(p.Framework)) flags.Add(p.Framework);

            var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            var indexed = p.IndexedAt != default ? $" (indexed: {p.IndexedAt:yyyy-MM-dd HH:mm})" : "";
            var lastCommit = syncStates.TryGetValue(p.Name, out var state) && !string.IsNullOrWhiteSpace(state.LastCommitSha)
                ? state.LastCommitSha
                : p.LastCommitSha;
            lines.Add($"- **{p.Name}**{flagStr}{indexed}");
            lines.Add($"  Repo: {p.RepoUrl ?? "(unknown)"}");
            lines.Add($"  Last commit: {(string.IsNullOrWhiteSpace(lastCommit) ? "(unknown)" : lastCommit)}");
        }

        return string.Join('\n', lines);
    }

    [McpServerTool(Name = "list_schemas", Title = "List Database Schemas", ReadOnly = true)]
    [Description("List indexed database schema projects with table/view/procedure counts. Use this before get_schema_catalog to discover available database schemas.")]
    public async Task<string> ListSchemas(
        [Description("Optional search across schema project, server, and database names")] string? search = null,
        [Description("Optional server name filter")] string? server = null,
        [Description("Optional database name filter")] string? database = null,
        [Description("Page number, 1-based")] int page = 1,
        [Description("Page size")] int pageSize = 25)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var result = await projectQuery.ListSchemasAsync(search, server, database, page, pageSize);
        if (result.Items.Count == 0)
            return JsonSerializer.Serialize(result, JsonOptions);

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "get_schema_catalog", Title = "Get Schema Catalog", ReadOnly = true)]
    [Description("Get a detailed catalog for an indexed database schema, including tables, views, procedures, columns, indexes, foreign keys, and parameters.")]
    public async Task<string> GetSchemaCatalog(
        [Description("Schema project name from list_schemas, for example db:server/database")] string name)
    {
        var result = await projectQuery.GetSchemaCatalogAsync(name);
        return result is null
            ? $"Schema project '{name}' was not found. Call list_schemas to discover available schemas."
            : JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "search_graph"),
     Description("Search for services, endpoints, models, events, or any code element by name pattern. Supports filtering by type and project. Results are ranked by trust score — untrusted nodes are deprioritized but not hidden.")]
    public Task<CallToolResult> SearchGraph(
        [Description("Name pattern to search for (supports % wildcards)")] string namePattern,
        [Description("Filter by node type: Class, Method, Route, Service, Event, Queue, Table, Interface, etc.")] string? label = null,
        [Description("Filter by project/repository name")] string? project = null,
        [Description("Max results (default 20)")] int limit = 20,
        [Description(ShaDescription)] string? sha = null)
        => NodeAnswerAsync(project, sha, reportsUnmapped: false, async (scope, scopedProject) =>
        {
            var (text, nodes, lowConfidenceCount) = await SearchGraphTextAsync(scope, namePattern, label, scopedProject, limit);
            return new NodeAnswer(text, nodes.Select(node => (string?)node.Project).Distinct(), lowConfidenceCount);
        });

    private async Task<(string Text, IReadOnlyList<GraphNode> Nodes, int LowConfidenceCount)> SearchGraphTextAsync(
        CommitScope scope,
        string namePattern,
        string? label,
        string? project,
        int limit)
    {
        var parsedLabel = label.TryParseEnum<NodeLabel>();

        // Fetch extra to account for trust-based reordering
        var result = await query.SearchAsync(new SearchRequest(namePattern, parsedLabel, project, Math.Min(limit * 2, 100)), scope.Graph);

        if (result.Nodes.Count == 0)
            return ($"No results found for '{namePattern}'.", [], 0);

        // Build trust lookup: file path → trust score (from file_metrics)
        var projects = result.Nodes.Select(n => n.Project).Distinct().ToList();
        var trustByFile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var proj in projects)
        {
            var summaries = await store.GetProjectHealthSummariesAsync(proj);
            var metrics = await store.GetFileMetricsAsync(proj);
            foreach (var m in metrics)
                trustByFile[$"{proj}::{m.FilePath}"] = m.TrustScore;
        }

        // Determine if this is an exact/direct search (no wildcards, specific name)
        var isDirectSearch = !namePattern.Contains('%') && !namePattern.Contains('*');

        // Rank nodes: exact name matches first, then by trust (unless direct search)
        var ranked = result.Nodes
            .Select(n =>
            {
                var isExactMatch = n.Name.Equals(namePattern, StringComparison.OrdinalIgnoreCase);
                // file_metrics describe the default-branch version of a file; a file changed at an
                // overlay commit has no metrics of its own, so it gets the neutral score.
                var changedAtCommit = scope.Overlay is { } overlay
                                      && overlay.IsOverlayProject(n.Project)
                                      && n.FilePath is not null
                                      && overlay.IsChangedPath(n.FilePath);
                var fileTrust = changedAtCommit ? 0.5 : trustByFile.GetValueOrDefault($"{n.Project}::{n.FilePath}", 0.5);
                var effectiveTrust = n.DoNotTrust ? 0.0 : fileTrust;
                return (Node: n, IsExact: isExactMatch, Trust: effectiveTrust);
            })
            .OrderByDescending(x => x.IsExact)                              // Exact matches first
            .ThenByDescending(x => isDirectSearch ? 0 : x.Trust)            // Trust ranking for fuzzy searches
            .ThenBy(x => x.Node.Name)                                       // Alphabetical tiebreak
            .Take(limit)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"## Search Results ({result.TotalCount} matches)\n");

        foreach (var (node, _, trust) in ranked)
        {
            var trustLabel = node.DoNotTrust ? " **[UNTRUSTED]**" : trust < 0.4 ? " _(low trust)_" : "";
            sb.AppendLine($"- **{node.Name}** ({node.Label}) — {node.Project}{trustLabel}{AnswerConfidence.Tag(node)}");
            if (!string.IsNullOrEmpty(node.FilePath))
                sb.AppendLine($"  File: {node.FilePath}:{node.StartLine}");
            if (!string.IsNullOrEmpty(node.QualifiedName) && node.QualifiedName != node.Name)
                sb.AppendLine($"  QN: {node.QualifiedName}");

            foreach (var prop in node.Properties.Where(p => p.Value is not null))
                sb.AppendLine($"  {prop.Key}: {prop.Value}");
        }

        return (sb.ToString(), result.Nodes, AnswerConfidence.Count(ranked.Select(entry => entry.Node)));
    }

    [McpServerTool(Name = "get_service_summary"),
     Description("Get the natural language description of a service/repository, including what it does, its endpoints, dependencies, and what depends on it.")]
    public async Task<string> GetServiceSummary(
        [Description("Project/repository name")] string project)
    {
        var summary = await store.GetRepositorySummaryAsync(project);
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
    public Task<CallToolResult> TraceCallPath(
        [Description("Function or method name to trace")] string functionName,
        [Description("Trace direction: inbound, outbound, or both (default: both)")] string direction = "both",
        [Description("How many levels deep to trace (default: 3)")] int depth = 3,
        [Description("Filter by project")] string? project = null,
        [Description(ShaDescription)] string? sha = null)
        => NodeAnswerAsync(project, sha, reportsUnmapped: true, async (scope, scopedProject) =>
        {
            var dir = direction.TryParseEnum<TraceDirection>() ?? TraceDirection.Both;

            var (start, result) = await query.TraceCallPathWithStartAsync(functionName, scopedProject, dir, depth, scope.Graph);
            var lowConfidenceCount = AnswerConfidence.Count(result.Select(entry => entry.Node).Append(start));
            int CountUnmapped(OverlayGraphView overlay)
                => overlay.CountUnmappedInboundEdges(result.Select(entry => entry.Node).Append(start), functionName,
                    [EdgeType.CALLS, EdgeType.HTTP_CALLS]);
            var startLine = start is not null && GraphNodeConfidence.IsLowConfidence(start)
                ? $"Start: **{start.Name}** ({start.Label}) — {start.Project}{AnswerConfidence.NodeTag}"
                : null;

            if (result.Count == 0)
            {
                var none = $"No call paths found for '{functionName}'.";
                return new NodeAnswer(startLine is null ? none : $"{none}\n{startLine}",
                    start is null ? [] : [start.Project], lowConfidenceCount, CountUnmapped: CountUnmapped);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"## Call Path: {functionName} ({direction})\n");
            if (startLine is not null)
                sb.AppendLine($"{startLine}\n");

            foreach (var entry in result)
            {
                var indent = new string(' ', entry.Depth * 2);
                sb.AppendLine($"{indent}- **{entry.Node.Name}** ({entry.Node.Label}) — {entry.Node.Project}{AnswerConfidence.Tag(entry.Node)}");
                sb.AppendLine($"{indent}  Edge: {entry.EdgeType}");
                if (!string.IsNullOrEmpty(entry.Node.FilePath))
                    sb.AppendLine($"{indent}  File: {entry.Node.FilePath}:{entry.Node.StartLine}");
            }

            return new NodeAnswer(sb.ToString(), ProjectsOf(result.Select(entry => entry.Node.Project)), lowConfidenceCount,
                CountUnmapped: CountUnmapped);
        });

    [McpServerTool(Name = "trace_data_lineage"),
     Description("Follow a data model from its database origin through all services that produce, transform, or consume it. Shows the complete data flow across the system.")]
    public Task<CallToolResult> TraceDataLineage(
        [Description("Model/DTO/event class name to trace")] string modelName,
        [Description("Filter by project")] string? project = null,
        [Description(ShaDescription)] string? sha = null)
        => NodeAnswerAsync(project, sha, reportsUnmapped: true, async (scope, scopedProject) =>
        {
            var result = await query.TraceDataLineageAsync(modelName, scopedProject, scope.Graph);
            var lowConfidenceCount = AnswerConfidence.Count(result.Producers.Concat(result.Consumers)
                .Select(entry => entry.Node).Append(result.ModelNode));
            int CountUnmapped(OverlayGraphView overlay) => overlay.CountUnmappedInboundEdges(
                result.Producers.Concat(result.Consumers).Select(entry => entry.Node).Append(result.ModelNode), modelName);

            if (result.Producers.Count == 0 && result.Consumers.Count == 0 && result.CrossRepoEdges.Count == 0)
                return new NodeAnswer($"No data lineage found for '{modelName}'.", [], lowConfidenceCount,
                    CountUnmapped: CountUnmapped);

            var sb = new StringBuilder();
            sb.AppendLine($"## Data Lineage: {modelName}\n");
            if (result.ModelNode is { } model && GraphNodeConfidence.IsLowConfidence(model))
                sb.AppendLine($"Model: **{model.Name}** ({model.Label}) — {model.Project}{AnswerConfidence.NodeTag}\n");

            if (result.Producers.Count > 0)
            {
                sb.AppendLine("### Producers (inbound)\n");
                foreach (var entry in result.Producers)
                {
                    var indent = new string(' ', entry.Depth * 2);
                    sb.AppendLine($"{indent}- **{entry.Node.Name}** ({entry.Node.Label}) — {entry.Node.Project} [{entry.EdgeType}]{AnswerConfidence.Tag(entry.Node)}");
                }
                sb.AppendLine();
            }

            if (result.Consumers.Count > 0)
            {
                sb.AppendLine("### Consumers (outbound)\n");
                foreach (var entry in result.Consumers)
                {
                    var indent = new string(' ', entry.Depth * 2);
                    sb.AppendLine($"{indent}- **{entry.Node.Name}** ({entry.Node.Label}) — {entry.Node.Project} [{entry.EdgeType}]{AnswerConfidence.Tag(entry.Node)}");
                }
                sb.AppendLine();
            }

            if (result.CrossRepoEdges.Count > 0)
            {
                sb.AppendLine("### Cross-Repo References\n");
                foreach (var edge in result.CrossRepoEdges)
                    sb.AppendLine($"- {edge.SourceProject} → {edge.TargetProject} ({edge.Type})");
            }

            return new NodeAnswer(sb.ToString(), ProjectsOf(result.Producers.Concat(result.Consumers).Select(entry => entry.Node.Project)
                .Concat(result.CrossRepoEdges.SelectMany(edge => new[] { edge.SourceProject, edge.TargetProject }))), lowConfidenceCount,
                CountUnmapped: CountUnmapped);
        });

    [McpServerTool(Name = "find_consumers"),
     Description("Find all services/methods that consume a given event, endpoint, or model. Shows cross-repo dependencies.")]
    public Task<CallToolResult> FindConsumers(
        [Description("Event, endpoint, or model name")] string name,
        [Description("Filter by project")] string? project = null,
        [Description(ShaDescription)] string? sha = null)
        => NodeAnswerAsync(project, sha, reportsUnmapped: true, async (scope, scopedProject) =>
        {
            var result = await query.FindConsumersAsync(name, scopedProject, scope.Graph);
            var lowConfidenceCount = result.Count(consumer => consumer.LowConfidence);
            // The consumers query reads one hop of inbound edges into the nodes named `name`.
            int CountUnmapped(OverlayGraphView overlay) => overlay.CountUnmappedInboundEdges(
                Array.Empty<GraphNode>(), name,
                [EdgeType.CONSUMES, EdgeType.CALLS, EdgeType.HTTP_CALLS, EdgeType.USES_TYPE, EdgeType.HANDLES]);

            if (result.Count == 0)
                return new NodeAnswer($"No consumers found for '{name}'.", [], 0, CountUnmapped: CountUnmapped);

            var sb = new StringBuilder();
            sb.AppendLine($"## Consumers of {name} ({result.Count})\n");

            foreach (var consumer in result)
            {
                sb.AppendLine($"- **{consumer.Name}** — {consumer.Project} ({consumer.EdgeType}){AnswerConfidence.Tag(consumer.LowConfidence)}");
                sb.AppendLine($"  QN: {consumer.QualifiedName}");
            }

            return new NodeAnswer(sb.ToString(), ProjectsOf(result.Select(consumer => consumer.Project)), lowConfidenceCount,
                CountUnmapped: CountUnmapped);
        });

    [McpServerTool(Name = "find_publishers"),
     Description("Find all services that publish to a given queue, exchange, or event type.")]
    public Task<CallToolResult> FindPublishers(
        [Description("Queue, exchange, or event name")] string name,
        [Description("Filter by project")] string? project = null,
        [Description(ShaDescription)] string? sha = null)
        => NodeAnswerAsync(project, sha, reportsUnmapped: true, async (scope, scopedProject) =>
        {
            var result = await query.FindPublishersAsync(name, scopedProject, scope.Graph);
            var lowConfidenceCount = result.Count(publisher => publisher.LowConfidence);
            // The publishers query reads one hop of inbound edges into the nodes named `name`.
            int CountUnmapped(OverlayGraphView overlay) => overlay.CountUnmappedInboundEdges(
                Array.Empty<GraphNode>(), name, [EdgeType.PUBLISHES, EdgeType.CALLS]);

            if (result.Count == 0)
                return new NodeAnswer($"No publishers found for '{name}'.", [], 0, CountUnmapped: CountUnmapped);

            var sb = new StringBuilder();
            sb.AppendLine($"## Publishers to {name} ({result.Count})\n");

            foreach (var publisher in result)
            {
                sb.AppendLine($"- **{publisher.Name}** — {publisher.Project} ({publisher.EdgeType}){AnswerConfidence.Tag(publisher.LowConfidence)}");
                sb.AppendLine($"  QN: {publisher.QualifiedName}");
            }

            return new NodeAnswer(sb.ToString(), ProjectsOf(result.Select(publisher => publisher.Project)), lowConfidenceCount,
                CountUnmapped: CountUnmapped);
        });

    private static IEnumerable<string?> ProjectsOf(IEnumerable<string> projects)
        => projects.Distinct(StringComparer.OrdinalIgnoreCase).Select(project => (string?)project).ToList();

    /// <summary>Every repository an impact report names: changed and affected nodes and cross-repo impacts.</summary>
    internal static IEnumerable<string> ImpactProjects(ImpactReport report)
        => report.ChangedNodes.Select(node => node.Project)
            .Concat(report.AffectedNodes.Select(node => node.Project))
            .Concat(report.CrossRepoImpacts.SelectMany(impact => new[] { impact.SourceProject, impact.TargetProject }));

    [McpServerTool(Name = "get_architecture"),
     Description("Get architecture overview for a project — hotspots, dependency analysis, complexity metrics.")]
    public async Task<string> GetArchitecture(
        [Description("Project name")] string project)
    {
        var report = await query.GetArchitectureAsync(project);

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

    [McpServerTool(Name = "get_project_health"),
     Description("Get health scores, file-level hotspots, and AI-generated health analysis for a repository. Shows overall health (1-10), hotspot files (high churn + complexity), and per-project breakdowns.")]
    public async Task<string> GetProjectHealth(
        [Description("Project/repository name")] string project,
        [Description("Number of top hotspot files to return (default 10)")] int topHotspots = 10)
    {
        var report = await query.GetProjectHealthAsync(project, topHotspots);

        if (report.RepoHealth is null && report.Analyses.Count == 0)
            return $"No health data available for '{project}'. Run vitals analysis first.";

        var sb = new StringBuilder();
        sb.AppendLine($"# Health Report: {project}\n");

        if (report.RepoHealth is not null)
        {
            var h = report.RepoHealth;
            sb.AppendLine($"**Overall Health:** {h.OverallHealth:F1}/10");
            sb.AppendLine($"**Total Files:** {h.TotalFiles}");
            sb.AppendLine($"**Hotspots:** {h.HotspotCount} (health < 4.0)");
            sb.AppendLine($"**Alerts:** {h.AlertCount} (health < 2.5)");
            if (!string.IsNullOrWhiteSpace(h.HistoryMaturity))
                sb.AppendLine($"**History Maturity:** {h.HistoryMaturity}");
            sb.AppendLine($"**Computed:** {h.ComputedAt:yyyy-MM-dd HH:mm}");
            sb.AppendLine();

            if (h.HasSufficientHistoryForTrends || !string.IsNullOrWhiteSpace(h.HistoryMaturity))
            {
                sb.AppendLine("## Repository Vitality\n");
                if (h.HasSufficientHistoryForTrends)
                {
                    if (!string.IsNullOrWhiteSpace(h.ActivityStatus))
                        sb.AppendLine($"- **Activity:** {h.ActivityStatus}");
                    if (!string.IsNullOrWhiteSpace(h.FirefightingStatus))
                        sb.AppendLine($"- **Firefighting:** {h.FirefightingStatus}");
                    sb.AppendLine($"- **Recent Velocity:** {h.VelocityLast6Months} commits vs {h.VelocityPrior6Months} in prior 6 months ({h.VelocityChangePercent:+0.0;-0.0;0.0}%)");
                    sb.AppendLine($"- **Dormancy:** {h.DormantMonths12m} zero-commit months in last year, max inactive streak {h.MaxInactiveStreakMonths}");
                    sb.AppendLine($"- **Firefighting Counts:** {h.FirefightingCommits90d} in 90d, {h.FirefightingCommits365d} in 365d");
                    sb.AppendLine($"- **Firefighting Rates:** {h.FirefightingRate90d:P0} in 90d, {h.FirefightingRate365d:P0} in 365d");
                }
                else
                {
                    sb.AppendLine("- Trend signals are immature due to limited repo history.");
                }
                sb.AppendLine();
            }
        }

        if (report.ProjectHealths.Count > 0)
        {
            sb.AppendLine("## Per-Project Health\n");
            foreach (var ph in report.ProjectHealths.OrderBy(p => p.OverallHealth))
            {
                sb.AppendLine($"- **{ph.DotnetProject}**: {ph.OverallHealth:F1}/10 ({ph.TotalFiles} files, {ph.HotspotCount} hotspots)");
            }
            sb.AppendLine();
        }

        if (report.TopHotspots.Count > 0)
        {
            sb.AppendLine("## Top Hotspot Files\n");
            foreach (var f in report.TopHotspots)
            {
                sb.AppendLine($"- **{f.FilePath}** — concern: {f.ConcernScore:F1}, health: {f.HealthScore:F1}, trust: {f.TrustScore:F2}");
                sb.AppendLine($"  Repeated fixes: {f.BugFixCommits365d:F2} ({f.BugFixRatio365d:P0}), recurring churn: {f.RecurringChurnScore:F2}, yearly churn: {f.Churn365d:F2}");
                sb.AppendLine($"  Complexity: {f.ComplexityScore}, recent churn: {f.Changes} changes, Authors: {f.AuthorCount}, Truck factor: {f.TruckFactor}, risk: {f.RiskScore:F0}");
            }
            sb.AppendLine();
        }

        if (report.Analyses.Count > 0)
        {
            sb.AppendLine("## AI Analysis\n");
            foreach (var a in report.Analyses)
            {
                if (!string.IsNullOrEmpty(a.DotnetProject))
                    sb.AppendLine($"### {a.DotnetProject} (confidence: {a.Confidence})\n");
                else
                    sb.AppendLine($"### Repository-level (confidence: {a.Confidence})\n");
                sb.AppendLine(a.Analysis);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_fleet_health"),
     Description("Get a health overview across all indexed repositories. Shows repos ranked by health score — useful for identifying which repos need the most attention. Optionally filter to only unhealthy repos.")]
    public async Task<string> GetFleetHealth(
        [Description("Only show repos with health below this threshold (default: show all)")] double? maxHealth = null,
        [Description("Max repos to return (default 50)")] int limit = 50)
    {
        var allSummaries = await store.GetAllRepoHealthSummariesAsync();
        var summaries = allSummaries
            .Where(s => s.DotnetProject == "")
            .OrderBy(s => s.OverallHealth)
            .ToList();

        if (summaries.Count == 0)
            return "No health data available. Run vitals analysis on repositories first.";

        if (maxHealth.HasValue)
            summaries = summaries.Where(s => s.OverallHealth <= maxHealth.Value).ToList();

        var displayed = summaries.Take(limit).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Fleet Health Overview ({summaries.Count} repos with data)\n");

        var avgHealth = summaries.Average(s => s.OverallHealth);
        var totalHotspots = summaries.Sum(s => s.HotspotCount);
        var totalAlerts = summaries.Sum(s => s.AlertCount);
        sb.AppendLine($"**Average Health:** {avgHealth:F1}/10");
        sb.AppendLine($"**Total Hotspots:** {totalHotspots}");
        sb.AppendLine($"**Total Alerts:** {totalAlerts}");
        sb.AppendLine($"**Repos below 4.0:** {summaries.Count(s => s.OverallHealth < 4.0)}");
        sb.AppendLine($"**Repos below 2.5:** {summaries.Count(s => s.OverallHealth < 2.5)}");
        sb.AppendLine();

        sb.AppendLine("## Repos by Health Score\n");
        foreach (var s in displayed)
        {
            var indicator = s.OverallHealth < 2.5 ? "🔴" : s.OverallHealth < 4.0 ? "🟡" : s.OverallHealth < 7.0 ? "🟢" : "✅";
            sb.AppendLine($"- {indicator} **{s.Project}**: {s.OverallHealth:F1}/10 ({s.TotalFiles} files, {s.HotspotCount} hotspots, {s.AlertCount} alerts)");
        }

        if (summaries.Count > limit)
            sb.AppendLine($"\n*...and {summaries.Count - limit} more repos*");

        return sb.ToString();
    }

    [McpServerTool(Name = "find_archival_candidates"),
     Description("Find repositories with no inbound or outbound dependencies — candidates for archival.")]
    public async Task<string> FindArchivalCandidates()
    {
        var candidates = await query.FindArchivalCandidatesAsync();

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
     Description("Read actual source code from a repository file. Use when the graph and summaries don't provide enough detail. With sha, reads the file as committed at that commit.")]
    public Task<CallToolResult> GetCodeSnippet(
        [Description("Project name")] string project,
        [Description("File path relative to repo root")] string filePath,
        [Description("Start line (0 for beginning)")] int startLine = 0,
        [Description("End line (0 for entire file)")] int endLine = 0,
        [Description(ShaDescription)] string? sha = null)
        => AnswerAsync(project, sha, async (scope, scopedProject) =>
        {
            var source = await commitScopes.ReadSourceAsync(scope, scopedProject ?? project, filePath);
            if (source.Status != CommitSourceStatus.Found)
            {
                return (scope.IsPinned
                    ? source.Message
                    : $"File not found: {project}/{filePath}. Check that the project is indexed and the file exists in the cache or local path.", []);
            }

            var lines = SplitLines(source.Content!);
            var start = startLine > 0 ? startLine - 1 : 0;
            var end = endLine > 0 ? Math.Min(endLine, lines.Length) : lines.Length;

            if (start >= lines.Length)
                return ($"Start line {startLine} exceeds file length ({lines.Length} lines).", []);

            var snippet = lines[start..end];
            var ext = Path.GetExtension(filePath).TrimStart('.');
            var sb = new StringBuilder();
            sb.AppendLine($"```{ext} // {filePath}:{start + 1}-{end}");
            for (var i = 0; i < snippet.Length; i++)
                sb.AppendLine($"{start + i + 1,5} | {snippet[i]}");
            sb.AppendLine("```");

            return (sb.ToString(), []);
        });

    [McpServerTool(Name = "read_node_source"),
     Description("Read the source code for a specific graph node by its ID. Returns the full file with the node's line range highlighted. Useful for understanding the implementation of a class, method, or other code element. With sha, reads the node and its file as of that commit.")]
    public Task<CallToolResult> ReadNodeSource(
        [Description("Node ID from the graph")] long nodeId,
        [Description(ShaDescription)] string? sha = null)
        => NodeAnswerAsync(null, sha, reportsUnmapped: false, async (scope, _) =>
        {
            var node = await scope.Graph.FindNodeByIdAsync(nodeId);
            if (node is null)
                return new NodeAnswer(scope.IsPinned ? $"Node {nodeId} not found at commit {scope.Sha}." : $"Node {nodeId} not found.", [], 0);

            var lowConfidenceCount = AnswerConfidence.Count([node]);
            if (string.IsNullOrWhiteSpace(node.FilePath))
                return new NodeAnswer($"Node '{node.Name}' ({node.Label}) has no source file path.", [node.Project], lowConfidenceCount);

            var source = await commitScopes.ReadSourceAsync(scope, node.Project, node.FilePath);
            if (source.Status != CommitSourceStatus.Found)
            {
                return new NodeAnswer(scope.IsPinned ? source.Message : $"Source file not found: {node.Project}/{node.FilePath}",
                    [node.Project], lowConfidenceCount);
            }

            var lines = SplitLines(source.Content!);

            // Return a window around the node: 5 lines before start through 5 lines after end,
            // or the full file if it's small
            var contextBefore = 5;
            var contextAfter = 5;
            var start = node.StartLine > 0 ? Math.Max(0, node.StartLine - 1 - contextBefore) : 0;
            var end = node.EndLine > 0 ? Math.Min(lines.Length, node.EndLine + contextAfter) : lines.Length;

            // If the file is small enough, just return it all
            if (lines.Length <= 200)
            {
                start = 0;
                end = lines.Length;
            }

            var ext = Path.GetExtension(node.FilePath).TrimStart('.');
            var sb = new StringBuilder();
            sb.AppendLine($"## {node.Name} ({node.Label}) — {node.Project}{AnswerConfidence.Tag(node)}");
            sb.AppendLine($"File: {node.FilePath}, lines {node.StartLine}–{node.EndLine}\n");
            sb.AppendLine($"```{ext}");
            for (var i = start; i < end; i++)
            {
                var marker = (i + 1 >= node.StartLine && i + 1 <= node.EndLine) ? "→" : " ";
                sb.AppendLine($"{marker}{i + 1,5} | {lines[i]}");
            }
            sb.AppendLine("```");

            return new NodeAnswer(sb.ToString(), [node.Project], lowConfidenceCount);
        });

    /// <summary>Splits text into lines like <see cref="StreamReader.ReadLine"/> (no trailing empty line).</summary>
    private static string[] SplitLines(string content)
    {
        var lines = new List<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    [McpServerTool(Name = "list_conventions"),
     Description("List all available company convention documents. These describe patterns, abstractions, and standards used across all ~620 repositories.")]
    public async Task<string> ListConventionsAsync()
    {
        var conventionsSection = await wikiStore.GetSectionBySlugAsync("conventions");
        if (conventionsSection is null)
            return "No convention documents found.";

        var pages = await wikiStore.GetPagesBySectionAsync(conventionsSection.Id);

        if (pages.Count == 0)
            return "No convention documents found.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Company Conventions ({pages.Count} documents)\n");
        sb.AppendLine("Use `get_convention` with the slug to read the full document.\n");

        foreach (var page in pages.OrderBy(p => p.Title))
            sb.AppendLine($"- **{page.Slug}** — {page.Title} (v{page.Revision}, by {page.Author})");

        return sb.ToString();
    }

    [McpServerTool(Name = "get_service_clusters"),
     Description("List automatically detected service clusters — groups of repos that are tightly coupled and function as a single system. Shows cluster members, edge density, modularity score, and bridge repos. Uses Louvain community detection on the cross-repo dependency graph.")]
    public async Task<string> GetServiceClusters()
    {
        var overview = await communityDetection.GetClusterOverviewAsync();

        if (overview.Clusters.Count == 0)
            return "No service clusters detected yet. Clusters are computed after repositories are indexed and cross-repo edges are linked.";

        var sb = new StringBuilder();
        sb.AppendLine($"# Service Clusters ({overview.Clusters.Count} communities)\n");
        sb.AppendLine($"**Modularity:** {overview.Modularity:F4} (higher = better separation)");
        sb.AppendLine($"**Clustered Projects:** {overview.ClusteredProjects} / {overview.TotalProjects}");
        if (overview.ComputedAt.HasValue)
            sb.AppendLine($"**Computed:** {overview.ComputedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var cluster in overview.Clusters.OrderByDescending(c => c.Members.Count))
        {
            var label = cluster.Label ?? $"Cluster {cluster.ClusterId}";
            sb.AppendLine($"## {label} ({cluster.Members.Count} repos)\n");
            sb.AppendLine($"Internal edges: {cluster.InternalEdgeCount} | External edges: {cluster.ExternalEdgeCount} | Density: {cluster.Density:F2}");

            if (cluster.BridgeRepos.Count > 0)
                sb.AppendLine($"Bridge repos: {string.Join(", ", cluster.BridgeRepos)}");

            sb.AppendLine();
            foreach (var member in cluster.Members.OrderBy(m => m))
                sb.AppendLine($"- {member}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_cluster_detail"),
     Description("Get detailed information about a specific service cluster — members with betweenness centrality, internal vs external edges, and connections to other clusters. Use get_service_clusters first to find cluster IDs.")]
    public async Task<string> GetClusterDetail(
        [Description("Cluster ID from get_service_clusters")] int clusterId)
    {
        var detail = await communityDetection.GetClusterDetailAsync(clusterId);

        if (detail is null)
            return $"Cluster {clusterId} not found. Use get_service_clusters to list available clusters.";

        var label = detail.Label ?? $"Cluster {detail.ClusterId}";
        var sb = new StringBuilder();
        sb.AppendLine($"# {label}\n");
        sb.AppendLine($"Internal edges: {detail.InternalEdgeCount} | External edges: {detail.ExternalEdgeCount}");
        sb.AppendLine();

        sb.AppendLine("## Members\n");
        foreach (var m in detail.Members.OrderByDescending(m => m.BetweennessCentrality))
        {
            var bridge = m.BetweennessCentrality > 0.01m ? " [BRIDGE]" : "";
            sb.AppendLine($"- **{m.ProjectName}** — betweenness: {m.BetweennessCentrality:F4}, internal: {m.InternalEdges}, external: {m.ExternalEdges}{bridge}");
        }
        sb.AppendLine();

        if (detail.TopConnections.Count > 0)
        {
            sb.AppendLine("## Connections to Other Clusters\n");
            foreach (var conn in detail.TopConnections)
            {
                var targetLabel = conn.TargetLabel ?? $"Cluster {conn.TargetClusterId}";
                sb.AppendLine($"- → **{targetLabel}**: {conn.EdgeCount} edges ({string.Join(", ", conn.EdgeTypes)})");
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "analyze_impact"),
     Description("Analyze the blast radius of changing a code element. Shows all affected nodes classified by risk (Critical/High/Medium/Low), cross-repo impact, and scope estimation. Use to assess risk before making changes.")]
    public Task<CallToolResult> AnalyzeImpact(
        [Description("Qualified name or name of the code element to analyze (e.g., 'OrderService.CreateOrder', 'OrderCreatedEvent')")] string name,
        [Description("Filter by project/repository")] string? project = null,
        [Description("Max traversal depth (default 3, max 5)")] int depth = 3,
        [Description(ShaDescription)] string? sha = null)
        => NodeAnswerAsync(project, sha, reportsUnmapped: true, async (scope, scopedProject) =>
        {
            var result = await impactAnalysis.AnalyzeImpactAsync(name, scopedProject, Math.Clamp(depth, 1, 5), scope.Graph);
            return result is null
                ? new NodeAnswer($"No nodes found matching '{name}'.", [], 0,
                    CountUnmapped: overlay => overlay.CountUnmappedInboundEdges(Array.Empty<GraphNode>(), name))
                : new NodeAnswer(FormatImpact(name, result), ProjectsOf(ImpactProjects(result)),
                    result.Summary.LowConfidenceCount, SummaryInText: true,
                    CountUnmapped: overlay => overlay.CountUnmappedInboundEdges(result, name));
        });

    private static string FormatImpact(string name, ImpactReport result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Blast Radius: {name}\n");

        // Summary
        var s = result.Summary;
        sb.AppendLine($"**Total affected:** {s.TotalAffected} nodes across {s.AffectedProjects.Count} project(s)");
        sb.AppendLine($"**Risk breakdown:** {s.CriticalCount} critical, {s.HighCount} high, {s.MediumCount} medium, {s.LowCount} low");
        if (s.CrossRepoCount > 0)
            sb.AppendLine($"**Cross-repo impact:** {s.CrossRepoCount} other project(s)");
        if (s.LowConfidenceCount > 0)
            sb.AppendLine($"**Low confidence:** this answer includes {s.LowConfidenceCount} low-confidence node(s) " +
                          "indexed by the tree-sitter fallback after semantic analysis failed; " +
                          "their relationships may be incomplete, so the blast radius may be understated.");
        sb.AppendLine();

        // Changed nodes
        if (result.ChangedNodes.Count > 0)
        {
            sb.AppendLine("## Changed Nodes\n");
            foreach (var n in result.ChangedNodes)
                sb.AppendLine($"- **{n.Name}** ({n.Label}) — {n.Project}{(n.LowConfidence ? " [low confidence]" : "")}");
            sb.AppendLine();
        }

        // Affected nodes grouped by risk
        foreach (var risk in new[] { RiskLevel.Critical, RiskLevel.High,
                                     RiskLevel.Medium, RiskLevel.Low })
        {
            var nodes = result.AffectedNodes.Where(n => n.Risk == risk).ToList();
            if (nodes.Count == 0) continue;

            sb.AppendLine($"## {risk} ({nodes.Count})\n");
            foreach (var n in nodes.OrderBy(n => n.Depth))
            {
                sb.AppendLine($"- **{n.Name}** ({n.Label}) — {n.Project} [hop {n.Depth}, {n.EdgeType}]" +
                              (n.LowConfidence ? " [low confidence]" : ""));
                if (n.RiskFactors.Count > 0)
                    sb.AppendLine($"  Factors: {string.Join("; ", n.RiskFactors)}");
            }
            sb.AppendLine();
        }

        // Cross-repo impact
        if (result.CrossRepoImpacts.Count > 0)
        {
            sb.AppendLine("## Cross-Repo Impact\n");
            foreach (var c in result.CrossRepoImpacts)
                sb.AppendLine($"- {c.SourceProject} → {c.TargetProject} ({c.EdgeType}, {c.AffectedNodeCount} nodes)");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_convention"),
     Description("Read a company convention document by slug. Conventions describe patterns like gateway calls, messaging, project structure, and other standards used across all repositories. Call list_conventions first to see available documents.")]
    public async Task<string> GetConventionAsync(
        [Description("Convention slug (e.g., 'gateway-pattern', 'messaging-pattern', 'project-structure')")] string name)
    {
        var conventionsSection = await wikiStore.GetSectionBySlugAsync("conventions");
        if (conventionsSection is null)
            return $"Convention '{name}' not found. Use list_conventions to see available documents.";

        // Try exact slug match first
        var page = await wikiStore.FindPageAsync(conventionsSection.Id, null, name);

        if (page is null)
        {
            // Fuzzy match on slug or title
            var candidates = await wikiStore.SearchPagesAsync(conventionsSection.Id, name);

            if (candidates.Count == 1)
                page = candidates[0];
            else if (candidates.Count > 1)
                return $"Multiple matches for '{name}': {string.Join(", ", candidates.Select(c => c.Slug))}. Be more specific.";
            else
                return $"Convention '{name}' not found. Use list_conventions to see available documents.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {page.Title}");
        sb.AppendLine($"*Revision {page.Revision} by {page.Author} — updated {page.UpdatedAt:yyyy-MM-dd}*\n");
        sb.AppendLine(page.Content);
        return sb.ToString();
    }

    [McpServerTool(Name = "search_conventions"),
     Description("Search company convention documents using hybrid BM25 lexical scoring and dense semantic retrieval over markdown-aware chunks.")]
    public async Task<string> SearchConventionsAsync(
        [Description("Natural-language query for the convention content.")] string query,
        [Description("Maximum number of chunks to return.")] int topK = 10)
    {
        if (conventionEmbeddingService is null)
            return "Convention semantic search is not configured.";

        var results = await conventionEmbeddingService.SearchAsync(query, topK);
        if (results.Count == 0)
            return "No convention chunks matched the search.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Convention Search Results ({results.Count})");
        sb.AppendLine();

        foreach (var result in results)
        {
            sb.AppendLine($"### {result.Title} / chunk {result.ChunkIndex}");
            sb.AppendLine($"- Slug: `{result.Slug}`");
            sb.AppendLine($"- Section: {result.SectionPath}");
            sb.AppendLine($"- Revision: {result.Revision}");
            sb.AppendLine($"- Score: {result.Score:F3}");
            sb.AppendLine();
            sb.AppendLine(result.Excerpt);
            sb.AppendLine();
        }

        return sb.ToString();
    }

}
