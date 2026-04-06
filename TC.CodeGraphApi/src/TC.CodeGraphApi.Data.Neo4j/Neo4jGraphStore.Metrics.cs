using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace TC.CodeGraphApi.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── File Metrics ──────────────────────────────────────────────────────

    public async Task UpsertFileMetricsBatchAsync(string project, IReadOnlyList<FileMetricsEntity> metrics)
    {
        if (metrics.Count == 0) return;

        await using var session = sessionFactory.GetSession();

        foreach (var chunk in Chunk(metrics, options.BatchSize))
        {
            var items = chunk.Select(m => new Dictionary<string, object?>
            {
                ["filePath"] = m.FilePath,
                ["dotnetProject"] = m.DotnetProject,
                ["changes"] = m.Changes,
                ["linesAdded"] = m.LinesAdded,
                ["linesRemoved"] = m.LinesRemoved,
                ["authorCount"] = m.AuthorCount,
                ["lastChangeAt"] = (object?)m.LastChangeAt,
                ["complexityScore"] = m.ComplexityScore,
                ["maxNestingDepth"] = m.MaxNestingDepth,
                ["deepNestingLines"] = m.DeepNestingLines,
                ["functionCount"] = m.FunctionCount,
                ["longestFunction"] = m.LongestFunction,
                ["lintErrors"] = m.LintErrors,
                ["lintWarnings"] = m.LintWarnings,
                ["trustScore"] = m.TrustScore,
                ["maxCouplingStrength"] = m.MaxCouplingStrength,
                ["couplingPartners"] = m.CouplingPartners,
                ["truckFactor"] = m.TruckFactor,
                ["topAuthors"] = m.TopAuthors,
                ["healthScore"] = m.HealthScore,
                ["role"] = m.Role,
                ["riskScore"] = m.RiskScore,
                ["computedAt"] = m.ComputedAt
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $items AS m
                    MERGE (fm:FileMetrics {project: $project, filePath: m.filePath})
                    SET fm.dotnetProject = m.dotnetProject,
                        fm.changes = m.changes, fm.linesAdded = m.linesAdded,
                        fm.linesRemoved = m.linesRemoved, fm.authorCount = m.authorCount,
                        fm.lastChangeAt = m.lastChangeAt,
                        fm.complexityScore = m.complexityScore,
                        fm.maxNestingDepth = m.maxNestingDepth,
                        fm.deepNestingLines = m.deepNestingLines,
                        fm.functionCount = m.functionCount, fm.longestFunction = m.longestFunction,
                        fm.lintErrors = m.lintErrors, fm.lintWarnings = m.lintWarnings,
                        fm.trustScore = m.trustScore,
                        fm.maxCouplingStrength = m.maxCouplingStrength,
                        fm.couplingPartners = m.couplingPartners,
                        fm.truckFactor = m.truckFactor, fm.topAuthors = m.topAuthors,
                        fm.healthScore = m.healthScore, fm.role = m.role,
                        fm.riskScore = m.riskScore, fm.computedAt = m.computedAt
                    """,
                    new { project, items });
            });
        }

        logger.LogInformation("Upserted {Count} file metrics for {Project}", metrics.Count, project);
    }

    public async Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(
        string project, string? dotnetProject = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = dotnetProject is null
                ? "MATCH (fm:FileMetrics {project: $project}) RETURN fm"
                : "MATCH (fm:FileMetrics {project: $project, dotnetProject: $dotnetProject}) RETURN fm";

            var cursor = await tx.RunAsync(cypher, new { project, dotnetProject });
            var results = new List<FileMetricsEntity>();
            await foreach (var record in cursor)
                results.Add(MapFileMetricsNode(record["fm"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string project, int top = 10)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (fm:FileMetrics {project: $project}) RETURN fm ORDER BY fm.riskScore DESC LIMIT $top",
                new { project, top });
            var results = new List<FileMetricsEntity>();
            await foreach (var record in cursor)
                results.Add(MapFileMetricsNode(record["fm"].As<INode>()));
            return results;
        });
    }

    public async Task DeleteFileMetricsAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (fm:FileMetrics {project: $project}) DELETE fm",
                new { project });
        });
    }

    // ── Project Health Summaries ──────────────────────────────────────────

    public async Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (h:ProjectHealthSummary {project: $project, dotnetProject: $dotnetProject})
                SET h.overallHealth = $overallHealth,
                    h.totalFiles = $totalFiles,
                    h.hotspotCount = $hotspotCount,
                    h.alertCount = $alertCount,
                    h.topHotspots = $topHotspots,
                    h.computedAt = $computedAt
                """,
                new
                {
                    project = summary.Project,
                    dotnetProject = summary.DotnetProject ?? "",
                    overallHealth = summary.OverallHealth,
                    totalFiles = summary.TotalFiles,
                    hotspotCount = summary.HotspotCount,
                    alertCount = summary.AlertCount,
                    topHotspots = summary.TopHotspots,
                    computedAt = summary.ComputedAt
                });
        });
    }

    public async Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (h:ProjectHealthSummary {project: $project}) RETURN h",
                new { project });
            var results = new List<ProjectHealthSummaryEntity>();
            await foreach (var record in cursor)
                results.Add(MapHealthSummaryNode(record["h"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (h:ProjectHealthSummary {dotnetProject: ''}) RETURN h ORDER BY h.overallHealth");
            var results = new List<ProjectHealthSummaryEntity>();
            await foreach (var record in cursor)
                results.Add(MapHealthSummaryNode(record["h"].As<INode>()));
            return results;
        });
    }

    // ── Project Health Analyses ───────────────────────────────────────────

    public async Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (h:ProjectHealthAnalysis {project: $project, dotnetProject: $dotnetProject})
                SET h.analysis = $analysis,
                    h.confidence = $confidence,
                    h.modelUsed = $modelUsed,
                    h.createdAt = COALESCE(h.createdAt, $now),
                    h.updatedAt = $now
                """,
                new
                {
                    project = analysis.Project,
                    dotnetProject = analysis.DotnetProject ?? "",
                    analysis = analysis.Analysis,
                    confidence = analysis.Confidence,
                    modelUsed = analysis.ModelUsed,
                    now = DateTime.UtcNow
                });
        });
    }

    public async Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (h:ProjectHealthAnalysis {project: $project}) RETURN h",
                new { project });
            var results = new List<ProjectHealthAnalysisEntity>();
            await foreach (var record in cursor)
            {
                var node = record["h"].As<INode>();
                results.Add(new ProjectHealthAnalysisEntity
                {
                    Project = node["project"].As<string>(),
                    DotnetProject = GetStringOrNull(node, "dotnetProject"),
                    Analysis = node["analysis"].As<string>(),
                    Confidence = node["confidence"].As<string>(),
                    ModelUsed = GetStringOrNull(node, "modelUsed"),
                    CreatedAt = GetDateTimeOrNull(node, "createdAt") ?? DateTime.MinValue,
                    UpdatedAt = GetDateTimeOrNull(node, "updatedAt") ?? DateTime.MinValue
                });
            }
            return results;
        });
    }

    // ── Mapping Helpers ───────────────────────────────────────────────────

    private static FileMetricsEntity MapFileMetricsNode(INode node) => new()
    {
        Project = node["project"].As<string>(),
        FilePath = node["filePath"].As<string>(),
        DotnetProject = GetStringOrNull(node, "dotnetProject"),
        Changes = node.Properties.ContainsKey("changes") ? node["changes"].As<int>() : 0,
        LinesAdded = node.Properties.ContainsKey("linesAdded") ? node["linesAdded"].As<int>() : 0,
        LinesRemoved = node.Properties.ContainsKey("linesRemoved") ? node["linesRemoved"].As<int>() : 0,
        AuthorCount = node.Properties.ContainsKey("authorCount") ? node["authorCount"].As<int>() : 0,
        LastChangeAt = GetDateTimeOrNull(node, "lastChangeAt"),
        ComplexityScore = node.Properties.ContainsKey("complexityScore") ? node["complexityScore"].As<int>() : 0,
        MaxNestingDepth = node.Properties.ContainsKey("maxNestingDepth") ? node["maxNestingDepth"].As<int>() : 0,
        DeepNestingLines = node.Properties.ContainsKey("deepNestingLines") ? node["deepNestingLines"].As<int>() : 0,
        FunctionCount = node.Properties.ContainsKey("functionCount") ? node["functionCount"].As<int>() : 0,
        LongestFunction = node.Properties.ContainsKey("longestFunction") ? node["longestFunction"].As<int>() : 0,
        LintErrors = node.Properties.ContainsKey("lintErrors") ? node["lintErrors"].As<int>() : 0,
        LintWarnings = node.Properties.ContainsKey("lintWarnings") ? node["lintWarnings"].As<int>() : 0,
        TrustScore = node.Properties.ContainsKey("trustScore") ? node["trustScore"].As<double>() : 0.5,
        MaxCouplingStrength = node.Properties.ContainsKey("maxCouplingStrength") ? node["maxCouplingStrength"].As<double>() : 0,
        CouplingPartners = node.Properties.ContainsKey("couplingPartners") ? node["couplingPartners"].As<int>() : 0,
        TruckFactor = node.Properties.ContainsKey("truckFactor") ? node["truckFactor"].As<int>() : 0,
        TopAuthors = GetStringOrNull(node, "topAuthors"),
        HealthScore = node.Properties.ContainsKey("healthScore") ? node["healthScore"].As<double>() : 5.0,
        Role = GetStringOrNull(node, "role") ?? "core",
        RiskScore = node.Properties.ContainsKey("riskScore") ? node["riskScore"].As<double>() : 0,
        ComputedAt = GetDateTimeOrNull(node, "computedAt") ?? DateTime.MinValue
    };

    private static ProjectHealthSummaryEntity MapHealthSummaryNode(INode node) => new()
    {
        Project = node["project"].As<string>(),
        DotnetProject = GetStringOrNull(node, "dotnetProject"),
        OverallHealth = node.Properties.ContainsKey("overallHealth") ? node["overallHealth"].As<double>() : 5.0,
        TotalFiles = node.Properties.ContainsKey("totalFiles") ? node["totalFiles"].As<int>() : 0,
        HotspotCount = node.Properties.ContainsKey("hotspotCount") ? node["hotspotCount"].As<int>() : 0,
        AlertCount = node.Properties.ContainsKey("alertCount") ? node["alertCount"].As<int>() : 0,
        TopHotspots = GetStringOrNull(node, "topHotspots"),
        ComputedAt = GetDateTimeOrNull(node, "computedAt") ?? DateTime.MinValue
    };
}
