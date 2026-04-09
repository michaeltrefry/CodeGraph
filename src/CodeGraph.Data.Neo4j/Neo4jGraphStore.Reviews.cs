using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Project Diagnostics ──────────────────────────────────────────────

    public async Task DeleteProjectDiagnosticsAsync(string project)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (d:ProjectDiagnostic {project: $project}) DELETE d",
                new { project });
        });
    }

    public async Task UpsertProjectDiagnosticsBatchAsync(string project, IReadOnlyList<ProjectDiagnosticEntity> diagnostics)
    {
        if (diagnostics.Count == 0) return;

        await using var session = sessionFactory.GetSession();

        foreach (var chunk in Chunk(diagnostics, options.BatchSize))
        {
            var items = chunk.Select(d => new Dictionary<string, object?>
            {
                ["dotnetProject"] = d.DotnetProject,
                ["source"] = d.Source,
                ["diagnosticKey"] = d.DiagnosticKey,
                ["diagnosticId"] = d.DiagnosticId,
                ["severity"] = d.Severity,
                ["message"] = d.Message,
                ["category"] = d.Category,
                ["filePath"] = d.FilePath,
                ["lineStart"] = (object?)d.LineStart,
                ["lineEnd"] = (object?)d.LineEnd,
                ["computedAt"] = d.ComputedAt
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $items AS d
                    MERGE (pd:ProjectDiagnostic {project: $project, diagnosticKey: d.diagnosticKey})
                    SET pd.dotnetProject = d.dotnetProject,
                        pd.source = d.source,
                        pd.diagnosticId = d.diagnosticId,
                        pd.severity = d.severity,
                        pd.message = d.message,
                        pd.category = d.category,
                        pd.filePath = d.filePath,
                        pd.lineStart = d.lineStart,
                        pd.lineEnd = d.lineEnd,
                        pd.computedAt = d.computedAt
                    """,
                    new { project, items });
            });
        }

        logger.LogInformation("Upserted {Count} project diagnostics for {Project}", diagnostics.Count, project);
    }

    public async Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(
        string project, string? dotnetProject = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = dotnetProject is null
                ? """
                  MATCH (d:ProjectDiagnostic {project: $project})
                  WITH d,
                       CASE d.severity
                           WHEN 'error' THEN 0
                           WHEN 'warning' THEN 1
                           WHEN 'info' THEN 2
                           WHEN 'suggestion' THEN 3
                           ELSE 4
                       END AS severityOrder
                  RETURN d ORDER BY severityOrder, d.filePath, coalesce(d.lineStart, 0), d.diagnosticId
                  """
                : """
                  MATCH (d:ProjectDiagnostic {project: $project, dotnetProject: $dotnetProject})
                  WITH d,
                       CASE d.severity
                           WHEN 'error' THEN 0
                           WHEN 'warning' THEN 1
                           WHEN 'info' THEN 2
                           WHEN 'suggestion' THEN 3
                           ELSE 4
                       END AS severityOrder
                  RETURN d ORDER BY severityOrder, d.filePath, coalesce(d.lineStart, 0), d.diagnosticId
                  """;

            var cursor = await tx.RunAsync(cypher, new { project, dotnetProject });
            var results = new List<ProjectDiagnosticEntity>();
            await foreach (var record in cursor)
            {
                var node = record["d"].As<INode>();
                results.Add(MapProjectDiagnosticNode(node));
            }

            return results;
        });
    }

    // ── Project Review Runs ──────────────────────────────────────────────

    public async Task<long> CreateProjectReviewRunAsync(ProjectReviewRunEntity run)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MERGE (seq:Sequence {name: 'project_review_run_id'})
                ON CREATE SET seq.value = 0
                SET seq.value = seq.value + 1
                WITH seq.value AS newId
                CREATE (r:ProjectReviewRun {
                    appId: newId,
                    project: $project,
                    projectName: $projectName,
                    reviewedCommitSha: $reviewedCommitSha,
                    status: $status,
                    reviewMode: $reviewMode,
                    promptVersion: $promptVersion,
                    overviewJson: $overviewJson,
                    modelUsed: $modelUsed,
                    createdAt: $createdAt,
                    startedAt: $startedAt,
                    completedAt: $completedAt,
                    error: $error
                })
                RETURN r.appId AS id
                """,
                new
                {
                    project = run.Project,
                    projectName = run.ProjectName,
                    reviewedCommitSha = (object?)run.ReviewedCommitSha,
                    status = run.Status,
                    reviewMode = run.ReviewMode,
                    promptVersion = run.PromptVersion,
                    overviewJson = (object?)run.OverviewJson,
                    modelUsed = (object?)run.ModelUsed,
                    createdAt = run.CreatedAt,
                    startedAt = (object?)run.StartedAt,
                    completedAt = (object?)run.CompletedAt,
                    error = (object?)run.Error
                });

            await cursor.FetchAsync();
            var id = cursor.Current["id"].As<long>();
            run.Id = id;
            return id;
        });
    }

    public async Task UpdateProjectReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null,
        DateTime? completedAt = null, string? error = null)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (r:ProjectReviewRun {appId: $reviewRunId})
                SET r.status = $status,
                    r.overviewJson = CASE WHEN $overviewJson IS NULL THEN r.overviewJson ELSE $overviewJson END,
                    r.completedAt = CASE WHEN $completedAt IS NULL THEN r.completedAt ELSE $completedAt END,
                    r.startedAt = CASE
                        WHEN $status IN ['running', 'completed', 'failed'] THEN COALESCE(r.startedAt, datetime())
                        ELSE r.startedAt
                    END,
                    r.error = $error
                """,
                new
                {
                    reviewRunId,
                    status,
                    overviewJson = (object?)overviewJson,
                    completedAt = (object?)completedAt,
                    error = (object?)error
                });
        });
    }

    public async Task UpsertProjectReviewFindingsAsync(long reviewRunId, IReadOnlyList<ProjectReviewFindingEntity> findings)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (r:ProjectReviewRun {appId: $reviewRunId})-[rel:HAS_FINDING]->(f:ProjectReviewFinding)
                DELETE rel, f
                """,
                new { reviewRunId });
        });

        if (findings.Count == 0) return;

        foreach (var finding in findings)
            finding.ReviewRunId = reviewRunId;

        foreach (var chunk in Chunk(findings, options.BatchSize))
        {
            var items = chunk.Select(f => new Dictionary<string, object?>
            {
                ["ordinal"] = f.Ordinal,
                ["severity"] = f.Severity,
                ["category"] = f.Category,
                ["title"] = f.Title,
                ["explanation"] = f.Explanation,
                ["evidence"] = f.Evidence,
                ["filePath"] = f.FilePath,
                ["lineStart"] = (object?)f.LineStart,
                ["lineEnd"] = (object?)f.LineEnd,
                ["suggestedImprovement"] = f.SuggestedImprovement,
                ["confidence"] = f.Confidence,
                ["provenanceJson"] = f.ProvenanceJson
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    MATCH (r:ProjectReviewRun {appId: $reviewRunId})
                    UNWIND $items AS f
                    MERGE (seq:Sequence {name: 'project_review_finding_id'})
                    ON CREATE SET seq.value = 0
                    SET seq.value = seq.value + 1
                    WITH r, f, seq.value AS newId
                    CREATE (pf:ProjectReviewFinding {
                        appId: newId,
                        reviewRunId: $reviewRunId,
                        ordinal: f.ordinal,
                        severity: f.severity,
                        category: f.category,
                        title: f.title,
                        explanation: f.explanation,
                        evidence: f.evidence,
                        filePath: f.filePath,
                        lineStart: f.lineStart,
                        lineEnd: f.lineEnd,
                        suggestedImprovement: f.suggestedImprovement,
                        confidence: f.confidence,
                        provenanceJson: f.provenanceJson
                    })
                    CREATE (r)-[:HAS_FINDING]->(pf)
                    """,
                    new { reviewRunId, items });
            });
        }

        logger.LogInformation("Upserted {Count} review findings for review run {ReviewRunId}", findings.Count, reviewRunId);
    }

    public async Task<ProjectReviewRunEntity?> GetLatestProjectReviewRunAsync(string project, string projectName)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (r:ProjectReviewRun {project: $project, projectName: $projectName})
                RETURN r
                ORDER BY r.createdAt DESC, r.appId DESC
                LIMIT 1
                """,
                new { project, projectName });

            if (await cursor.FetchAsync())
                return MapProjectReviewRunNode(cursor.Current["r"].As<INode>());

            return null;
        });
    }

    public async Task<ProjectReviewRunEntity?> GetProjectReviewRunAsync(long reviewRunId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (r:ProjectReviewRun {appId: $reviewRunId})
                RETURN r
                LIMIT 1
                """,
                new { reviewRunId });

            if (await cursor.FetchAsync())
                return MapProjectReviewRunNode(cursor.Current["r"].As<INode>());

            return null;
        });
    }

    public async Task<IReadOnlyList<ProjectReviewFindingEntity>> GetProjectReviewFindingsAsync(long reviewRunId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (:ProjectReviewRun {appId: $reviewRunId})-[:HAS_FINDING]->(f:ProjectReviewFinding)
                RETURN f
                ORDER BY coalesce(f.ordinal, 0), f.appId
                """,
                new { reviewRunId });

            var results = new List<ProjectReviewFindingEntity>();
            await foreach (var record in cursor)
            {
                var node = record["f"].As<INode>();
                results.Add(MapProjectReviewFindingNode(node));
            }

            return results;
        });
    }

    // ── Mapping Helpers ──────────────────────────────────────────────────

    private static ProjectDiagnosticEntity MapProjectDiagnosticNode(INode node) => new()
    {
        Project = node["project"].As<string>(),
        DotnetProject = GetStringOrNull(node, "dotnetProject"),
        Source = GetStringOrNull(node, "source") ?? "roslyn",
        DiagnosticKey = node["diagnosticKey"].As<string>(),
        DiagnosticId = node["diagnosticId"].As<string>(),
        Severity = node["severity"].As<string>(),
        Message = node["message"].As<string>(),
        Category = GetStringOrNull(node, "category"),
        FilePath = GetStringOrNull(node, "filePath") ?? "",
        LineStart = node.Properties.TryGetValue("lineStart", out var lineStart) && lineStart is not null
            ? lineStart.As<int?>()
            : null,
        LineEnd = node.Properties.TryGetValue("lineEnd", out var lineEnd) && lineEnd is not null
            ? lineEnd.As<int?>()
            : null,
        ComputedAt = GetDateTimeOrNull(node, "computedAt") ?? DateTime.MinValue
    };

    private static ProjectReviewRunEntity MapProjectReviewRunNode(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        Project = node["project"].As<string>(),
        ProjectName = node["projectName"].As<string>(),
        ReviewedCommitSha = GetStringOrNull(node, "reviewedCommitSha"),
        Status = node["status"].As<string>(),
        ReviewMode = GetStringOrNull(node, "reviewMode") ?? "standard",
        PromptVersion = GetStringOrNull(node, "promptVersion") ?? "v1",
        OverviewJson = GetStringOrNull(node, "overviewJson"),
        ModelUsed = GetStringOrNull(node, "modelUsed"),
        CreatedAt = GetDateTimeOrNull(node, "createdAt") ?? DateTime.MinValue,
        StartedAt = GetDateTimeOrNull(node, "startedAt"),
        CompletedAt = GetDateTimeOrNull(node, "completedAt"),
        Error = GetStringOrNull(node, "error")
    };

    private static ProjectReviewFindingEntity MapProjectReviewFindingNode(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        ReviewRunId = node["reviewRunId"].As<long>(),
        Ordinal = node.Properties.TryGetValue("ordinal", out var ordinal) && ordinal is not null
            ? ordinal.As<int>()
            : 0,
        Severity = node["severity"].As<string>(),
        Category = node["category"].As<string>(),
        Title = node["title"].As<string>(),
        Explanation = node["explanation"].As<string>(),
        Evidence = node["evidence"].As<string>(),
        FilePath = GetStringOrNull(node, "filePath") ?? "",
        LineStart = node.Properties.TryGetValue("lineStart", out var lineStart) && lineStart is not null
            ? lineStart.As<int?>()
            : null,
        LineEnd = node.Properties.TryGetValue("lineEnd", out var lineEnd) && lineEnd is not null
            ? lineEnd.As<int?>()
            : null,
        SuggestedImprovement = GetStringOrNull(node, "suggestedImprovement") ?? "",
        Confidence = GetStringOrNull(node, "confidence") ?? "",
        ProvenanceJson = GetStringOrNull(node, "provenanceJson")
    };
}
