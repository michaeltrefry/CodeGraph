using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

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
                ["concernScore"] = m.ConcernScore,
                ["churn30d"] = m.Churn30d,
                ["churn90d"] = m.Churn90d,
                ["churn365d"] = m.Churn365d,
                ["bugFixCommits90d"] = m.BugFixCommits90d,
                ["bugFixCommits365d"] = m.BugFixCommits365d,
                ["bugFixRatio365d"] = m.BugFixRatio365d,
                ["bugFixWeightedTouches365d"] = m.BugFixWeightedTouches365d,
                ["recurringChurnScore"] = m.RecurringChurnScore,
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
                        fm.riskScore = m.riskScore,
                        fm.concernScore = m.concernScore,
                        fm.churn30d = m.churn30d,
                        fm.churn90d = m.churn90d,
                        fm.churn365d = m.churn365d,
                        fm.bugFixCommits90d = m.bugFixCommits90d,
                        fm.bugFixCommits365d = m.bugFixCommits365d,
                        fm.bugFixRatio365d = m.bugFixRatio365d,
                        fm.bugFixWeightedTouches365d = m.bugFixWeightedTouches365d,
                        fm.recurringChurnScore = m.recurringChurnScore,
                        fm.computedAt = m.computedAt
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
                "MATCH (fm:FileMetrics {project: $project}) RETURN fm ORDER BY COALESCE(fm.concernScore, 0) DESC, COALESCE(fm.riskScore, 0) DESC LIMIT $top",
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
                    h.historyMaturity = $historyMaturity,
                    h.hasSufficientHistoryForTrends = $hasSufficientHistoryForTrends,
                    h.activityStatus = $activityStatus,
                    h.firefightingStatus = $firefightingStatus,
                    h.monthlyCommitCounts = $monthlyCommitCounts,
                    h.velocityLast6Months = $velocityLast6Months,
                    h.velocityPrior6Months = $velocityPrior6Months,
                    h.velocityChangePercent = $velocityChangePercent,
                    h.dormantMonths12m = $dormantMonths12m,
                    h.maxInactiveStreakMonths = $maxInactiveStreakMonths,
                    h.firefightingCommits90d = $firefightingCommits90d,
                    h.firefightingCommits365d = $firefightingCommits365d,
                    h.firefightingRate90d = $firefightingRate90d,
                    h.firefightingRate365d = $firefightingRate365d,
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
                    historyMaturity = summary.HistoryMaturity,
                    hasSufficientHistoryForTrends = summary.HasSufficientHistoryForTrends,
                    activityStatus = summary.ActivityStatus,
                    firefightingStatus = summary.FirefightingStatus,
                    monthlyCommitCounts = summary.MonthlyCommitCounts,
                    velocityLast6Months = summary.VelocityLast6Months,
                    velocityPrior6Months = summary.VelocityPrior6Months,
                    velocityChangePercent = summary.VelocityChangePercent,
                    dormantMonths12m = summary.DormantMonths12m,
                    maxInactiveStreakMonths = summary.MaxInactiveStreakMonths,
                    firefightingCommits90d = summary.FirefightingCommits90d,
                    firefightingCommits365d = summary.FirefightingCommits365d,
                    firefightingRate90d = summary.FirefightingRate90d,
                    firefightingRate365d = summary.FirefightingRate365d,
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
        ConcernScore = node.Properties.ContainsKey("concernScore") ? node["concernScore"].As<double>() : 0,
        Churn30d = node.Properties.ContainsKey("churn30d") ? node["churn30d"].As<double>() : 0,
        Churn90d = node.Properties.ContainsKey("churn90d") ? node["churn90d"].As<double>() : 0,
        Churn365d = node.Properties.ContainsKey("churn365d") ? node["churn365d"].As<double>() : 0,
        BugFixCommits90d = node.Properties.ContainsKey("bugFixCommits90d") ? node["bugFixCommits90d"].As<double>() : 0,
        BugFixCommits365d = node.Properties.ContainsKey("bugFixCommits365d") ? node["bugFixCommits365d"].As<double>() : 0,
        BugFixRatio365d = node.Properties.ContainsKey("bugFixRatio365d") ? node["bugFixRatio365d"].As<double>() : 0,
        BugFixWeightedTouches365d = node.Properties.ContainsKey("bugFixWeightedTouches365d") ? node["bugFixWeightedTouches365d"].As<double>() : 0,
        RecurringChurnScore = node.Properties.ContainsKey("recurringChurnScore") ? node["recurringChurnScore"].As<double>() : 0,
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
        HistoryMaturity = GetStringOrNull(node, "historyMaturity"),
        HasSufficientHistoryForTrends = node.Properties.ContainsKey("hasSufficientHistoryForTrends") && node["hasSufficientHistoryForTrends"].As<bool>(),
        ActivityStatus = GetStringOrNull(node, "activityStatus"),
        FirefightingStatus = GetStringOrNull(node, "firefightingStatus"),
        MonthlyCommitCounts = GetStringOrNull(node, "monthlyCommitCounts"),
        VelocityLast6Months = node.Properties.ContainsKey("velocityLast6Months") ? node["velocityLast6Months"].As<int>() : 0,
        VelocityPrior6Months = node.Properties.ContainsKey("velocityPrior6Months") ? node["velocityPrior6Months"].As<int>() : 0,
        VelocityChangePercent = node.Properties.ContainsKey("velocityChangePercent") ? node["velocityChangePercent"].As<double>() : 0,
        DormantMonths12m = node.Properties.ContainsKey("dormantMonths12m") ? node["dormantMonths12m"].As<int>() : 0,
        MaxInactiveStreakMonths = node.Properties.ContainsKey("maxInactiveStreakMonths") ? node["maxInactiveStreakMonths"].As<int>() : 0,
        FirefightingCommits90d = node.Properties.ContainsKey("firefightingCommits90d") ? node["firefightingCommits90d"].As<int>() : 0,
        FirefightingCommits365d = node.Properties.ContainsKey("firefightingCommits365d") ? node["firefightingCommits365d"].As<int>() : 0,
        FirefightingRate90d = node.Properties.ContainsKey("firefightingRate90d") ? node["firefightingRate90d"].As<double>() : 0,
        FirefightingRate365d = node.Properties.ContainsKey("firefightingRate365d") ? node["firefightingRate365d"].As<double>() : 0,
        ComputedAt = GetDateTimeOrNull(node, "computedAt") ?? DateTime.MinValue
    };
}
