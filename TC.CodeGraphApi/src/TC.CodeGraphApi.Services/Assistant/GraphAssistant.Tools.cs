using System.Text;
using System.Text.Json;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services.Extensions;
using TC.CodeGraphApi.Services.Query;

namespace TC.CodeGraphApi.Services.Assistant;

public partial class GraphAssistant
{
    private async Task<string> SearchGraphAsync(JsonElement input)
    {
        var pattern = GetString(input, "namePattern") ?? "%";
        var label = GetString(input, "label");
        var project = GetString(input, "project");
        var limit = GetInt(input, "limit") ?? 20;

        var parsedLabel = label.TryParseEnum<NodeLabel>();

        var result = await query.SearchAsync(new SearchRequest(pattern, parsedLabel, project, Math.Min(limit * 2, 100)));

        if (result.Nodes.Count == 0)
            return $"No results found for '{pattern}'.";

        // Trust-weighted ranking: exact matches first, untrusted last (for fuzzy searches)
        var isDirectSearch = !pattern.Contains('%') && !pattern.Contains('*');
        var ranked = result.Nodes
            .Select(n => (Node: n, IsExact: n.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.IsExact)
            .ThenBy(x => isDirectSearch ? 0 : (x.Node.DoNotTrust ? 1 : 0))
            .ThenBy(x => x.Node.Name)
            .Take(limit)
            .Select(x => x.Node)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"## Search Results ({result.TotalCount} matches)\n");
        foreach (var node in ranked)
        {
            var trustLabel = node.DoNotTrust ? " **[UNTRUSTED]**" : "";
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

    private async Task<string> ListProjectsAsync()
    {
        var projects = await store.ListRepositoriesAsync();
        if (projects.Count == 0) return "No projects indexed yet.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Indexed Projects ({projects.Count})\n");
        foreach (var p in projects.OrderBy(p => p.Name))
        {
            var flags = new List<string>();
            if (p.IsFoundational) flags.Add("foundational");
            if (!string.IsNullOrEmpty(p.Language)) flags.Add(p.Language);
            var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            var indexed = p.IndexedAt.HasValue ? $" (indexed: {p.IndexedAt:yyyy-MM-dd})" : "";
            sb.AppendLine($"- **{p.Name}**{flagStr}{indexed}");
        }
        return sb.ToString();
    }

    private async Task<string> GetServiceSummaryAsync(JsonElement input)
    {
        var project = GetString(input, "project") ?? "";
        var summary = await store.GetRepositorySummaryAsync(project);
        if (summary is null) return $"No analysis available for '{project}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {project}");
        sb.AppendLine($"**Confidence:** {summary.Confidence}");
        sb.AppendLine($"**Last updated:** {summary.UpdatedAt:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine(summary.Summary);
        return sb.ToString();
    }

    private async Task<string> TraceCallPathAsync(JsonElement input)
    {
        var fn = GetString(input, "functionName") ?? "";
        var dirStr = GetString(input, "direction") ?? "both";
        var depth = GetInt(input, "depth") ?? 3;
        var project = GetString(input, "project");

        var dir = dirStr.TryParseEnum<TraceDirection>() ?? TraceDirection.Both;

        var result = await query.TraceCallPathAsync(fn, project, dir, depth);
        if (result.Count == 0) return $"No call paths found for '{fn}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Call Path: {fn} ({dirStr})\n");
        foreach (var entry in result)
        {
            var indent = new string(' ', entry.Depth * 2);
            sb.AppendLine($"{indent}- **{entry.Node.Name}** ({entry.Node.Label}) — {entry.Node.Project} [{entry.EdgeType}]");
        }
        return sb.ToString();
    }

    private async Task<string> TraceDataLineageAsync(JsonElement input)
    {
        var model = GetString(input, "modelName") ?? "";
        var project = GetString(input, "project");
        var result = await query.TraceDataLineageAsync(model, project);

        if (result.Producers.Count == 0 && result.Consumers.Count == 0 && result.CrossRepoEdges.Count == 0)
            return $"No data lineage found for '{model}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Data Lineage: {model}\n");
        if (result.Producers.Count > 0)
        {
            sb.AppendLine("### Producers\n");
            foreach (var e in result.Producers)
                sb.AppendLine($"- **{e.Node.Name}** ({e.Node.Label}) — {e.Node.Project} [{e.EdgeType}]");
        }
        if (result.Consumers.Count > 0)
        {
            sb.AppendLine("### Consumers\n");
            foreach (var e in result.Consumers)
                sb.AppendLine($"- **{e.Node.Name}** ({e.Node.Label}) — {e.Node.Project} [{e.EdgeType}]");
        }
        if (result.CrossRepoEdges.Count > 0)
        {
            sb.AppendLine("### Cross-Repo\n");
            foreach (var e in result.CrossRepoEdges)
                sb.AppendLine($"- {e.SourceProject} → {e.TargetProject} ({e.Type})");
        }
        return sb.ToString();
    }

    private async Task<string> FindConsumersAsync(JsonElement input)
    {
        var name = GetString(input, "name") ?? "";
        var project = GetString(input, "project");
        var result = await query.FindConsumersAsync(name, project);
        if (result.Count == 0) return $"No consumers found for '{name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Consumers of {name} ({result.Count})\n");
        foreach (var c in result)
            sb.AppendLine($"- **{c.Name}** — {c.Project} ({c.EdgeType})");
        return sb.ToString();
    }

    private async Task<string> FindPublishersAsync(JsonElement input)
    {
        var name = GetString(input, "name") ?? "";
        var project = GetString(input, "project");
        var result = await query.FindPublishersAsync(name, project);
        if (result.Count == 0) return $"No publishers found for '{name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Publishers to {name} ({result.Count})\n");
        foreach (var p in result)
            sb.AppendLine($"- **{p.Name}** — {p.Project} ({p.EdgeType})");
        return sb.ToString();
    }

    private async Task<string> GetArchitectureAsync(JsonElement input)
    {
        var project = GetString(input, "project") ?? "";
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
        sb.AppendLine("### Node Counts");
        foreach (var (label, count) in report.NodeCounts.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"- {label}: {count}");
        if (report.Hotspots.Count > 0)
        {
            sb.AppendLine("\n### Hotspots (high fan-in)");
            foreach (var h in report.Hotspots)
                sb.AppendLine($"- **{h.Name}** — {h.FanIn} callers");
        }
        if (report.InboundDependencies.Count > 0)
        {
            sb.AppendLine($"\n### Inbound from {report.InboundDependencies.Select(e => e.SourceProject).Distinct().Count()} projects");
        }
        if (report.OutboundDependencies.Count > 0)
        {
            sb.AppendLine($"\n### Outbound to {report.OutboundDependencies.Select(e => e.TargetProject).Distinct().Count()} projects");
        }
        return sb.ToString();
    }

    private async Task<string> FindArchivalCandidatesAsync()
    {
        var candidates = await query.FindArchivalCandidatesAsync();
        if (candidates.Count == 0) return "No archival candidates found.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Archival Candidates ({candidates.Count})\n");
        foreach (var p in candidates)
            sb.AppendLine($"- **{p.Name}** (indexed: {p.IndexedAt:yyyy-MM-dd})");
        return sb.ToString();
    }

    private async Task<string> GetProjectHealthAsync(JsonElement input)
    {
        var project = GetString(input, "project") ?? "";
        var top = GetInt(input, "topHotspots") ?? 10;
        var report = await query.GetProjectHealthAsync(project, top);

        if (report.RepoHealth is null && report.Analyses.Count == 0)
            return $"No health data available for '{project}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# Health: {project}\n");

        if (report.RepoHealth is not null)
        {
            var h = report.RepoHealth;
            sb.AppendLine($"**Overall Health:** {h.OverallHealth:F1}/10");
            sb.AppendLine($"**Files:** {h.TotalFiles}, **Hotspots:** {h.HotspotCount}, **Alerts:** {h.AlertCount}");
            sb.AppendLine();
        }

        if (report.ProjectHealths.Count > 0)
        {
            sb.AppendLine("### Per-Project");
            foreach (var ph in report.ProjectHealths.OrderBy(p => p.OverallHealth))
                sb.AppendLine($"- **{ph.DotnetProject}**: {ph.OverallHealth:F1}/10 ({ph.HotspotCount} hotspots)");
            sb.AppendLine();
        }

        if (report.TopHotspots.Count > 0)
        {
            sb.AppendLine("### Top Hotspots");
            foreach (var f in report.TopHotspots)
                sb.AppendLine($"- **{f.FilePath}** — health: {f.HealthScore:F1}, trust: {f.TrustScore:F2}, risk: {f.RiskScore:F0}, complexity: {f.ComplexityScore}, churn: {f.Changes}");
            sb.AppendLine();
        }

        if (report.Analyses.Count > 0)
        {
            sb.AppendLine("### Analysis");
            foreach (var a in report.Analyses)
            {
                if (!string.IsNullOrEmpty(a.DotnetProject))
                    sb.AppendLine($"**{a.DotnetProject}** ({a.Confidence}):");
                sb.AppendLine(a.Analysis);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private async Task<string> GetFleetHealthAsync(JsonElement input)
    {
        var maxHealth = input.TryGetProperty("maxHealth", out var mh) && mh.ValueKind == JsonValueKind.Number
            ? (double?)mh.GetDouble()
            : null;
        var limit = GetInt(input, "limit") ?? 50;

        var summaries = await store.GetAllRepoHealthSummariesAsync();

        if (summaries.Count == 0)
            return "No health data available.";

        var filtered = maxHealth.HasValue
            ? summaries.Where(s => s.OverallHealth <= maxHealth.Value).ToList()
            : summaries;

        var displayed = filtered.OrderBy(s => s.OverallHealth).Take(limit).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Fleet Health ({summaries.Count} repos)\n");
        sb.AppendLine($"**Avg Health:** {summaries.Average(s => s.OverallHealth):F1}/10");
        sb.AppendLine($"**Total Hotspots:** {summaries.Sum(s => s.HotspotCount)}");
        sb.AppendLine($"**Repos below 4.0:** {summaries.Count(s => s.OverallHealth < 4.0)}");
        sb.AppendLine();

        foreach (var s in displayed)
            sb.AppendLine($"- **{s.Project}**: {s.OverallHealth:F1}/10 ({s.TotalFiles} files, {s.HotspotCount} hotspots)");

        if (filtered.Count > limit)
            sb.AppendLine($"\n*...and {filtered.Count - limit} more*");

        return sb.ToString();
    }

    private async Task<string> ReadNodeSourceAsync(JsonElement input)
    {
        var nodeId = GetInt(input, "nodeId") ?? 0;
        if (nodeId == 0) return "nodeId is required.";

        var node = await store.FindNodeByIdAsync(nodeId);
        if (node is null) return $"Node {nodeId} not found.";
        if (string.IsNullOrWhiteSpace(node.FilePath))
            return $"Node '{node.Name}' ({node.Label}) has no source file path.";

        var fullPath = await RepoFileResolver.ResolveAsync(node.Project, node.FilePath, gitLabOptions, store);
        if (fullPath is null)
            return $"Source file not found: {node.Project}/{node.FilePath}";

        var lines = await File.ReadAllLinesAsync(fullPath);
        var start = node.StartLine > 0 ? Math.Max(0, node.StartLine - 1 - 5) : 0;
        var end = node.EndLine > 0 ? Math.Min(lines.Length, node.EndLine + 5) : lines.Length;
        if (lines.Length <= 200) { start = 0; end = lines.Length; }

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

    private async Task<string> GetCodeSnippetAsync(JsonElement input)
    {
        var project = GetString(input, "project") ?? "";
        var filePath = GetString(input, "filePath") ?? "";
        var startLine = GetInt(input, "startLine") ?? 0;
        var endLine = GetInt(input, "endLine") ?? 0;

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(filePath))
            return "project and filePath are required.";

        var fullPath = await RepoFileResolver.ResolveAsync(project, filePath, gitLabOptions, store);
        if (fullPath is null)
            return $"File not found: {project}/{filePath}";

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

    private static string GetGraphSchema() => """
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

    private async Task<string> ListConventionsAsync()
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

    private async Task<string> GetConventionAsync(JsonElement input)
    {
        var name = GetString(input, "name") ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return "name is required.";

        var conventionsSection = await wikiStore.GetSectionBySlugAsync("conventions");
        if (conventionsSection is null)
            return $"Convention '{name}' not found. Use list_conventions to see available documents.";

        var page = await wikiStore.FindPageAsync(conventionsSection.Id, null, name);

        if (page is null)
        {
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
