using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Query;
using CodeGraph.Services.Extensions;

namespace CodeGraph.Services.Assistant;

public partial class GraphAssistant
{
    private async Task<string> SearchGraphAsync(JsonElement input)
    {
        var pattern = GetString(input, "namePattern") ?? "%";
        var label = GetString(input, "label");
        var repository = GetString(input, "repository");
        var limit = GetInt(input, "limit") ?? 20;

        var parsedLabel = label.TryParseEnum<NodeLabel>();

        var result = await query.SearchAsync(new SearchRequest(pattern, parsedLabel, repository, Math.Min(limit * 2, 100)));

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
            sb.AppendLine($"- **{node.Name}** ({node.Label}) — {node.Repository}{trustLabel}{AnswerConfidence.Tag(node)}");
            if (!string.IsNullOrEmpty(node.FilePath))
                sb.AppendLine($"  File: {node.FilePath}:{node.StartLine}");
            if (!string.IsNullOrEmpty(node.QualifiedName) && node.QualifiedName != node.Name)
                sb.AppendLine($"  QN: {node.QualifiedName}");
            foreach (var prop in node.Properties.Where(p => p.Value is not null))
                sb.AppendLine($"  {prop.Key}: {prop.Value}");
        }
        AppendLowConfidenceLine(sb, AnswerConfidence.Count(ranked));
        return sb.ToString();
    }

    private async Task<string> ListRepositoriesAsync()
    {
        var repositories = await store.ListRepositoriesAsync();
        if (repositories.Count == 0) return "No repositories indexed yet.";

        return FormatRepositories(repositories.OrderBy(p => p.Name).ToList(), repositories.Count, includeRepoUrl: false);
    }

    private async Task<string> SearchRepositoriesAsync(JsonElement input)
    {
        var search = GetString(input, "search");
        var group = GetString(input, "group");
        var page = Math.Max(1, GetInt(input, "page") ?? 1);
        var pageSize = Math.Clamp(GetInt(input, "pageSize") ?? 25, 1, 100);

        var result = await store.SearchRepositoriesAsync(search, group, page, pageSize);
        if (result.TotalCount == 0)
            return string.IsNullOrWhiteSpace(search) && string.IsNullOrWhiteSpace(group)
                ? "No repositories indexed yet."
                : "No repositories matched the search.";

        return FormatRepositories(result.Items, result.TotalCount, includeRepoUrl: true);
    }

    private static string FormatRepositories(IReadOnlyList<RepositoryInfo> repositories, int totalCount, bool includeRepoUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Indexed Repositories ({totalCount})\n");
        foreach (var p in repositories)
        {
            var flags = new List<string>();
            if (p.IsFoundational) flags.Add("foundational");
            if (!string.IsNullOrEmpty(p.Language)) flags.Add(p.Language);
            if (!string.IsNullOrEmpty(p.Framework)) flags.Add(p.Framework);
            var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            var indexed = p.IndexedAt.HasValue ? $" (indexed: {p.IndexedAt:yyyy-MM-dd})" : "";
            sb.AppendLine($"- **{p.Name}**{flagStr}{indexed}");
            if (includeRepoUrl)
                sb.AppendLine($"  Repo: {p.RepoUrl ?? "(unknown)"}");
        }
        return sb.ToString();
    }

    private async Task<string> GetServiceSummaryAsync(JsonElement input)
    {
        var repository = GetString(input, "repository") ?? "";
        var summary = await store.GetRepositorySummaryAsync(repository);
        if (summary is null) return $"No analysis available for '{repository}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {repository}");
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
        var repository = GetString(input, "repository");

        var dir = dirStr.TryParseEnum<TraceDirection>() ?? TraceDirection.Both;

        var (start, result) = await query.TraceCallPathWithStartAsync(fn, repository, dir, depth);
        var startLine = start is not null && GraphNodeConfidence.IsLowConfidence(start)
            ? $"Start: **{start.Name}** ({start.Label}) — {start.Repository}{AnswerConfidence.NodeTag}"
            : null;
        var lowConfidenceCount = AnswerConfidence.Count(result.Select(entry => entry.Node).Append(start));
        if (result.Count == 0)
        {
            var none = new StringBuilder($"No call paths found for '{fn}'.\n");
            if (startLine is not null)
                none.AppendLine(startLine);
            AppendLowConfidenceLine(none, lowConfidenceCount);
            return none.ToString().TrimEnd();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Call Path: {fn} ({dirStr})\n");
        if (startLine is not null)
            sb.AppendLine($"{startLine}\n");
        foreach (var entry in result)
        {
            var indent = new string(' ', entry.Depth * 2);
            sb.AppendLine($"{indent}- **{entry.Node.Name}** ({entry.Node.Label}) — {entry.Node.Repository} [{entry.EdgeType}]{AnswerConfidence.Tag(entry.Node)}");
        }
        AppendLowConfidenceLine(sb, lowConfidenceCount);
        return sb.ToString();
    }

    private async Task<string> TraceDataLineageAsync(JsonElement input)
    {
        var model = GetString(input, "modelName") ?? "";
        var repository = GetString(input, "repository");
        var result = await query.TraceDataLineageAsync(model, repository);

        if (result.Producers.Count == 0 && result.Consumers.Count == 0 && result.CrossRepoEdges.Count == 0)
            return $"No data lineage found for '{model}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Data Lineage: {model}\n");
        if (result.Producers.Count > 0)
        {
            sb.AppendLine("### Producers\n");
            foreach (var e in result.Producers)
                sb.AppendLine($"- **{e.Node.Name}** ({e.Node.Label}) — {e.Node.Repository} [{e.EdgeType}]{AnswerConfidence.Tag(e.Node)}");
        }
        if (result.Consumers.Count > 0)
        {
            sb.AppendLine("### Consumers\n");
            foreach (var e in result.Consumers)
                sb.AppendLine($"- **{e.Node.Name}** ({e.Node.Label}) — {e.Node.Repository} [{e.EdgeType}]{AnswerConfidence.Tag(e.Node)}");
        }
        if (result.CrossRepoEdges.Count > 0)
        {
            sb.AppendLine("### Cross-Repo\n");
            foreach (var e in result.CrossRepoEdges)
                sb.AppendLine($"- {e.SourceRepository} → {e.TargetRepository} ({e.Type})");
        }
        AppendLowConfidenceLine(sb, AnswerConfidence.Count(result.Producers.Concat(result.Consumers)
            .Select(entry => entry.Node).Append(result.ModelNode)));
        return sb.ToString();
    }

    private async Task<string> FindConsumersAsync(JsonElement input)
    {
        var name = GetString(input, "name") ?? "";
        var repository = GetString(input, "repository");
        var result = await query.FindConsumersAsync(name, repository);
        if (result.Count == 0) return $"No consumers found for '{name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Consumers of {name} ({result.Count})\n");
        foreach (var c in result)
            sb.AppendLine($"- **{c.Name}** — {c.Repository} ({c.EdgeType}){AnswerConfidence.Tag(c.LowConfidence)}");
        AppendLowConfidenceLine(sb, result.Count(c => c.LowConfidence));
        return sb.ToString();
    }

    private async Task<string> FindPublishersAsync(JsonElement input)
    {
        var name = GetString(input, "name") ?? "";
        var repository = GetString(input, "repository");
        var result = await query.FindPublishersAsync(name, repository);
        if (result.Count == 0) return $"No publishers found for '{name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Publishers to {name} ({result.Count})\n");
        foreach (var p in result)
            sb.AppendLine($"- **{p.Name}** — {p.Repository} ({p.EdgeType}){AnswerConfidence.Tag(p.LowConfidence)}");
        AppendLowConfidenceLine(sb, result.Count(p => p.LowConfidence));
        return sb.ToString();
    }

    private static void AppendLowConfidenceLine(StringBuilder sb, int lowConfidenceCount)
    {
        if (AnswerConfidence.SummaryLine(lowConfidenceCount) is { } line)
            sb.AppendLine().AppendLine(line);
    }

    private async Task<string> GetArchitectureAsync(JsonElement input)
    {
        var repository = GetString(input, "repository") ?? "";
        return ArchitectureReportFormatter.Format(await query.GetArchitectureAsync(repository));
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

    private async Task<string> GetRepositoryHealthAsync(JsonElement input)
    {
        var repository = GetString(input, "repository") ?? "";
        var top = GetInt(input, "topHotspots") ?? 10;
        var report = await query.GetRepositoryHealthAsync(repository, top);

        if (report.RepoHealth is null && report.Analyses.Count == 0)
            return $"No health data available for '{repository}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# Health: {repository}\n");

        if (report.RepoHealth is not null)
        {
            var h = report.RepoHealth;
            sb.AppendLine($"**Overall Health:** {h.OverallHealth:F1}/10");
            sb.AppendLine($"**Files:** {h.TotalFiles}, **Hotspots:** {h.HotspotCount}, **Alerts:** {h.AlertCount}");
            if (!string.IsNullOrWhiteSpace(h.HistoryMaturity))
                sb.AppendLine($"**History Maturity:** {h.HistoryMaturity}");
            sb.AppendLine();

            if (h.HasSufficientHistoryForTrends || !string.IsNullOrWhiteSpace(h.HistoryMaturity))
            {
                sb.AppendLine("### Repository Vitality");
                if (h.HasSufficientHistoryForTrends)
                {
                    if (!string.IsNullOrWhiteSpace(h.ActivityStatus))
                        sb.AppendLine($"- **Activity:** {h.ActivityStatus}");
                    if (!string.IsNullOrWhiteSpace(h.FirefightingStatus))
                        sb.AppendLine($"- **Firefighting:** {h.FirefightingStatus}");
                    sb.AppendLine($"- **Recent Velocity:** {h.VelocityLast6Months} vs {h.VelocityPrior6Months} commits over the last two 6-month windows ({h.VelocityChangePercent:+0.0;-0.0;0.0}%)");
                    sb.AppendLine($"- **Dormancy:** {h.DormantMonths12m} zero-commit months in the last year, max inactive streak {h.MaxInactiveStreakMonths}");
                    sb.AppendLine($"- **Firefighting Rates:** {h.FirefightingRate90d:P0} in 90d, {h.FirefightingRate365d:P0} in 365d");
                }
                else
                {
                    sb.AppendLine("- Trend signals are immature due to limited repo history.");
                }
                sb.AppendLine();
            }
        }

        if (report.DotnetProjectHealths.Count > 0)
        {
            sb.AppendLine("### Per-Project");
            foreach (var ph in report.DotnetProjectHealths.OrderBy(p => p.OverallHealth))
                sb.AppendLine($"- **{ph.DotnetProject}**: {ph.OverallHealth:F1}/10 ({ph.HotspotCount} hotspots)");
            sb.AppendLine();
        }

        if (report.TopHotspots.Count > 0)
        {
            sb.AppendLine("### Top Hotspots");
            foreach (var f in report.TopHotspots)
                sb.AppendLine($"- **{f.FilePath}** — concern: {f.ConcernScore:F1}, health: {f.HealthScore:F1}, fixes: {f.BugFixCommits365d:F2} ({f.BugFixRatio365d:P0}), recurring churn: {f.RecurringChurnScore:F2}, risk: {f.RiskScore:F0}");
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
            sb.AppendLine($"- **{s.Repository}**: {s.OverallHealth:F1}/10 ({s.TotalFiles} files, {s.HotspotCount} hotspots)");

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

        var lines = await RepoFileResolver.ReadAllLinesAsync(node.Repository, node.FilePath, sourceOptions, store);
        if (lines is null)
            return $"Source file not found: {node.Repository}/{node.FilePath}";
        var start = node.StartLine > 0 ? Math.Max(0, node.StartLine - 1 - 5) : 0;
        var end = node.EndLine > 0 ? Math.Min(lines.Length, node.EndLine + 5) : lines.Length;
        if (lines.Length <= 200) { start = 0; end = lines.Length; }

        var ext = Path.GetExtension(node.FilePath).TrimStart('.');
        var sb = new StringBuilder();
        sb.AppendLine($"## {node.Name} ({node.Label}) — {node.Repository}");
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
        var repository = GetString(input, "repository") ?? "";
        var filePath = GetString(input, "filePath") ?? "";
        var startLine = GetInt(input, "startLine") ?? 0;
        var endLine = GetInt(input, "endLine") ?? 0;

        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(filePath))
            return "repository and filePath are required.";

        var lines = await RepoFileResolver.ReadAllLinesAsync(repository, filePath, sourceOptions, store);
        if (lines is null)
            return $"File not found: {repository}/{filePath}";
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

    private async Task<string> SearchConventionsAsync(JsonElement input)
    {
        if (conventionEmbeddingService is null)
            return "Convention semantic search is not configured.";

        var query = GetString(input, "query") ?? "";
        if (string.IsNullOrWhiteSpace(query))
            return "query is required.";

        var topK = GetInt(input, "topK") ?? 10;
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
