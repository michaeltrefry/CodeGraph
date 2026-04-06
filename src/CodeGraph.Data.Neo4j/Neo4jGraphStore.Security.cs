using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Security Findings ─────────────────────────────────────────────────

    public async Task DeleteSecurityFindingsAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (f:SecurityFinding {project: $project}) DELETE f",
                new { project });
        });
    }

    public async Task UpsertSecurityFindingsBatchAsync(string project, IReadOnlyList<SecurityFindingEntity> findings)
    {
        if (findings.Count == 0) return;

        await using var session = sessionFactory.GetSession();

        foreach (var chunk in Chunk(findings, options.BatchSize))
        {
            var items = chunk.Select(f => new Dictionary<string, object?>
            {
                ["dotnetProject"] = f.DotnetProject,
                ["category"] = f.Category,
                ["severity"] = f.Severity,
                ["title"] = f.Title,
                ["description"] = f.Description,
                ["filePath"] = f.FilePath,
                ["lineNumber"] = (object?)f.LineNumber,
                ["package"] = f.Package,
                ["packageVersion"] = f.PackageVersion,
                ["advisory"] = f.Advisory,
                ["computedAt"] = f.ComputedAt
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $items AS f
                    CREATE (sf:SecurityFinding {
                        project: $project,
                        dotnetProject: f.dotnetProject,
                        category: f.category,
                        severity: f.severity,
                        title: f.title,
                        description: f.description,
                        filePath: f.filePath,
                        lineNumber: f.lineNumber,
                        package: f.package,
                        packageVersion: f.packageVersion,
                        advisory: f.advisory,
                        computedAt: f.computedAt
                    })
                    """,
                    new { project, items });
            });
        }

        logger.LogInformation("Upserted {Count} security findings for {Project}", findings.Count, project);
    }

    public async Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            // Order by severity (critical first)
            var cursor = await tx.RunAsync("""
                MATCH (f:SecurityFinding {project: $project})
                WITH f,
                     CASE f.severity
                         WHEN 'critical' THEN 0
                         WHEN 'high' THEN 1
                         WHEN 'medium' THEN 2
                         WHEN 'low' THEN 3
                         ELSE 4
                     END AS severityOrder
                RETURN f ORDER BY severityOrder
                """,
                new { project });

            var results = new List<SecurityFindingEntity>();
            await foreach (var record in cursor)
            {
                var node = record["f"].As<INode>();
                results.Add(new SecurityFindingEntity
                {
                    Project = node["project"].As<string>(),
                    DotnetProject = GetStringOrNull(node, "dotnetProject"),
                    Category = node["category"].As<string>(),
                    Severity = node["severity"].As<string>(),
                    Title = node["title"].As<string>(),
                    Description = node["description"].As<string>(),
                    FilePath = GetStringOrNull(node, "filePath"),
                    LineNumber = node.Properties.ContainsKey("lineNumber") && node["lineNumber"] is not null
                        ? node["lineNumber"].As<int?>() : null,
                    Package = GetStringOrNull(node, "package"),
                    PackageVersion = GetStringOrNull(node, "packageVersion"),
                    Advisory = GetStringOrNull(node, "advisory"),
                    ComputedAt = GetDateTimeOrNull(node, "computedAt") ?? DateTime.MinValue
                });
            }
            return results;
        });
    }

    // ── Project Security Summaries ────────────────────────────────────────

    public async Task UpsertProjectSecuritySummaryAsync(ProjectSecuritySummaryEntity summary)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (s:ProjectSecuritySummary {project: $project})
                SET s.securityScore = $securityScore,
                    s.criticalCount = $criticalCount,
                    s.highCount = $highCount,
                    s.mediumCount = $mediumCount,
                    s.lowCount = $lowCount,
                    s.analysis = $analysis,
                    s.computedAt = $computedAt
                """,
                new
                {
                    project = summary.Project,
                    securityScore = summary.SecurityScore,
                    criticalCount = summary.CriticalCount,
                    highCount = summary.HighCount,
                    mediumCount = summary.MediumCount,
                    lowCount = summary.LowCount,
                    analysis = summary.Analysis,
                    computedAt = summary.ComputedAt
                });
        });
    }

    public async Task<ProjectSecuritySummaryEntity?> GetProjectSecuritySummaryAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (s:ProjectSecuritySummary {project: $project}) RETURN s",
                new { project });
            if (await cursor.FetchAsync())
            {
                var node = cursor.Current["s"].As<INode>();
                return new ProjectSecuritySummaryEntity
                {
                    Project = node["project"].As<string>(),
                    SecurityScore = node.Properties.ContainsKey("securityScore") ? node["securityScore"].As<double>() : 10.0,
                    CriticalCount = node.Properties.ContainsKey("criticalCount") ? node["criticalCount"].As<int>() : 0,
                    HighCount = node.Properties.ContainsKey("highCount") ? node["highCount"].As<int>() : 0,
                    MediumCount = node.Properties.ContainsKey("mediumCount") ? node["mediumCount"].As<int>() : 0,
                    LowCount = node.Properties.ContainsKey("lowCount") ? node["lowCount"].As<int>() : 0,
                    Analysis = GetStringOrNull(node, "analysis"),
                    ComputedAt = GetDateTimeOrNull(node, "computedAt") ?? DateTime.MinValue
                };
            }
            return null;
        });
    }
}
