using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;
using CodeGraph.Services.Extensions;

namespace CodeGraph.Services.Assistant;

[McpServerToolType]
public class CodeGraphMcpServer(
    GraphQueryEngine query,
    IGraphStore store,
    IWikiStore wikiStore,
    GitLabOptions gitLabOptions,
    ICommunityDetectionService communityDetection,
    IImpactAnalysisService impactAnalysis,
    ILogger<CodeGraphMcpServer> logger)
{

    [McpServerTool(Name = "get_graph_schema"),
     Description("Describe the available node types, edge types, and their properties in the knowledge graph.")]
    public string GetGraphSchema()
    {
        return """
            ## Node Types
            Repository, DotnetProject, Namespace, Folder, File, Class, Interface,
            Enum, Struct, Record, Function, Method, Property, Constructor, Delegate,
            Route, Service, Table, View, StoredProcedure, Event, Queue, Exchange,
            Component, Module, Job, NuGetPackage

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

    [McpServerTool(Name = "list_projects"),
     Description("List all indexed repositories with metadata: last indexed date, language, whether foundational.")]
    public async Task<string> ListProjects()
    {
        var projects = await store.ListRepositoriesAsync();
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
     Description("Search for services, endpoints, models, events, or any code element by name pattern. Supports filtering by type and project. Results are ranked by trust score — untrusted nodes are deprioritized but not hidden.")]
    public async Task<string> SearchGraph(
        [Description("Name pattern to search for (supports % wildcards)")] string namePattern,
        [Description("Filter by node type: Class, Method, Route, Service, Event, Queue, Table, Interface, etc.")] string? label = null,
        [Description("Filter by project/repository name")] string? project = null,
        [Description("Max results (default 20)")] int limit = 20)
    {
        var parsedLabel = label.TryParseEnum<NodeLabel>();

        // Fetch extra to account for trust-based reordering
        var result = await query.SearchAsync(new SearchRequest(namePattern, parsedLabel, project, Math.Min(limit * 2, 100)));

        if (result.Nodes.Count == 0)
            return $"No results found for '{namePattern}'.";

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
                var fileTrust = trustByFile.GetValueOrDefault($"{n.Project}::{n.FilePath}", 0.5);
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
            sb.AppendLine($"- **{node.Name}** ({node.Label}) — {node.Project}{trustLabel}");
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
    public async Task<string> TraceCallPath(
        [Description("Function or method name to trace")] string functionName,
        [Description("Trace direction: inbound, outbound, or both (default: both)")] string direction = "both",
        [Description("How many levels deep to trace (default: 3)")] int depth = 3,
        [Description("Filter by project")] string? project = null)
    {
        var dir = direction.TryParseEnum<TraceDirection>() ?? TraceDirection.Both;

        var result = await query.TraceCallPathAsync(functionName, project, dir, depth);

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
        var result = await query.TraceDataLineageAsync(modelName, project);

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
        var result = await query.FindConsumersAsync(name, project);

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
        var result = await query.FindPublishersAsync(name, project);

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
     Description("Get health scores, file-level hotspots, and Claude-generated health analysis for a repository. Shows overall health (1-10), hotspot files (high churn + complexity), and per-project breakdowns.")]
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
            sb.AppendLine($"**Computed:** {h.ComputedAt:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
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
                sb.AppendLine($"- **{f.FilePath}** — health: {f.HealthScore:F1}, trust: {f.TrustScore:F2}, risk: {f.RiskScore:F0}");
                sb.AppendLine($"  Complexity: {f.ComplexityScore}, Churn: {f.Changes} changes, Authors: {f.AuthorCount}, Truck factor: {f.TruckFactor}");
            }
            sb.AppendLine();
        }

        if (report.Analyses.Count > 0)
        {
            sb.AppendLine("## Claude Analysis\n");
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
     Description("Read actual source code from a repository file. Use when the graph and summaries don't provide enough detail.")]
    public async Task<string> GetCodeSnippet(
        [Description("Project name")] string project,
        [Description("File path relative to repo root")] string filePath,
        [Description("Start line (0 for beginning)")] int startLine = 0,
        [Description("End line (0 for entire file)")] int endLine = 0)
    {
        var fullPath = await RepoFileResolver.ResolveAsync(project, filePath, gitLabOptions, store);
        if (fullPath is null)
            return $"File not found: {project}/{filePath}. Check that the project is indexed and the file exists in the cache or local path.";

        var lines = await File.ReadAllLinesAsync(fullPath);
        var start = startLine > 0 ? startLine - 1 : 0;
        var end = endLine > 0 ? Math.Min(endLine, lines.Length) : lines.Length;

        if (start >= lines.Length)
            return $"Start line {startLine} exceeds file length ({lines.Length} lines).";

        var snippet = lines[start..end];
        var ext = Path.GetExtension(filePath).TrimStart('.');
        var sb = new StringBuilder();
        sb.AppendLine($"```{ext} // {filePath}:{start + 1}-{end}");
        for (var i = 0; i < snippet.Length; i++)
            sb.AppendLine($"{start + i + 1,5} | {snippet[i]}");
        sb.AppendLine("```");

        return sb.ToString();
    }

    [McpServerTool(Name = "read_node_source"),
     Description("Read the source code for a specific graph node by its ID. Returns the full file with the node's line range highlighted. Useful for understanding the implementation of a class, method, or other code element.")]
    public async Task<string> ReadNodeSource(
        [Description("Node ID from the graph")] long nodeId)
    {
        var node = await store.FindNodeByIdAsync(nodeId);
        if (node is null)
            return $"Node {nodeId} not found.";

        if (string.IsNullOrWhiteSpace(node.FilePath))
            return $"Node '{node.Name}' ({node.Label}) has no source file path.";

        var fullPath = await RepoFileResolver.ResolveAsync(node.Project, node.FilePath, gitLabOptions, store);
        if (fullPath is null)
            return $"Source file not found: {node.Project}/{node.FilePath}";

        var lines = await File.ReadAllLinesAsync(fullPath);

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
        sb.AppendLine($"## {node.Name} ({node.Label}) — {node.Project}");
        sb.AppendLine($"File: {node.FilePath}, lines {node.StartLine}–{node.EndLine}\n");
        sb.AppendLine($"```{ext}");
        for (var i = start; i < end; i++)
        {
            var marker = (i + 1 >= node.StartLine && i + 1 <= node.EndLine) ? "→" : " ";
            sb.AppendLine($"{marker}{i + 1,5} | {lines[i]}");
        }
        sb.AppendLine("```");

        return sb.ToString();
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
    public async Task<string> AnalyzeImpact(
        [Description("Qualified name or name of the code element to analyze (e.g., 'OrderService.CreateOrder', 'OrderCreatedEvent')")] string name,
        [Description("Filter by project/repository")] string? project = null,
        [Description("Max traversal depth (default 3, max 5)")] int depth = 3)
    {
        var result = await impactAnalysis.AnalyzeImpactAsync(name, project, Math.Clamp(depth, 1, 5));

        if (result is null)
            return $"No nodes found matching '{name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# Blast Radius: {name}\n");

        // Summary
        var s = result.Summary;
        sb.AppendLine($"**Total affected:** {s.TotalAffected} nodes across {s.AffectedProjects.Count} project(s)");
        sb.AppendLine($"**Risk breakdown:** {s.CriticalCount} critical, {s.HighCount} high, {s.MediumCount} medium, {s.LowCount} low");
        if (s.CrossRepoCount > 0)
            sb.AppendLine($"**Cross-repo impact:** {s.CrossRepoCount} other project(s)");
        sb.AppendLine();

        // Changed nodes
        if (result.ChangedNodes.Count > 0)
        {
            sb.AppendLine("## Changed Nodes\n");
            foreach (var n in result.ChangedNodes)
                sb.AppendLine($"- **{n.Name}** ({n.Label}) — {n.Project}");
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
                sb.AppendLine($"- **{n.Name}** ({n.Label}) — {n.Project} [hop {n.Depth}, {n.EdgeType}]");
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

}
