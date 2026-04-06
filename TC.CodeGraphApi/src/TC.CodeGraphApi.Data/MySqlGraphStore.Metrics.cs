using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;

namespace TC.CodeGraphApi.Data;

public partial class MySqlGraphStore
{
    // ── File Metrics (Vitals) ─────────────────────────────────────────────

    public async Task UpsertFileMetricsBatchAsync(string project, IReadOnlyList<FileMetricsEntity> metrics)
    {
        if (metrics.Count == 0) return;

        await WithDeadlockRetryAsync(async () =>
        {
            await using var conn = await GetOpenConnectionAsync();

            foreach (var chunk in Chunk(metrics, 500))
            {
                var sb = new StringBuilder();
                sb.AppendLine("""
                    INSERT INTO file_metrics
                        (project, file_path, dotnet_project, changes, lines_added, lines_removed,
                         author_count, last_change_at, complexity_score, max_nesting_depth,
                         deep_nesting_lines, function_count, longest_function,
                         lint_errors, lint_warnings, trust_score,
                         max_coupling_strength, coupling_partners, truck_factor, top_authors,
                         health_score, role, risk_score, computed_at)
                    VALUES
                    """);

                var parameters = new DynamicParameters();
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.AppendLine($"""
                        (@project_{i}, @file_path_{i}, @dotnet_project_{i}, @changes_{i},
                         @lines_added_{i}, @lines_removed_{i}, @author_count_{i}, @last_change_at_{i},
                         @complexity_score_{i}, @max_nesting_depth_{i}, @deep_nesting_lines_{i},
                         @function_count_{i}, @longest_function_{i},
                         @lint_errors_{i}, @lint_warnings_{i}, @trust_score_{i},
                         @max_coupling_strength_{i},
                         @coupling_partners_{i}, @truck_factor_{i}, @top_authors_{i},
                         @health_score_{i}, @role_{i}, @risk_score_{i}, @computed_at_{i})
                        """);

                    var m = chunk[i];
                    parameters.Add($"project_{i}", project);
                    parameters.Add($"file_path_{i}", m.FilePath);
                    parameters.Add($"dotnet_project_{i}", m.DotnetProject);
                    parameters.Add($"changes_{i}", m.Changes);
                    parameters.Add($"lines_added_{i}", m.LinesAdded);
                    parameters.Add($"lines_removed_{i}", m.LinesRemoved);
                    parameters.Add($"author_count_{i}", m.AuthorCount);
                    parameters.Add($"last_change_at_{i}", m.LastChangeAt);
                    parameters.Add($"complexity_score_{i}", m.ComplexityScore);
                    parameters.Add($"max_nesting_depth_{i}", m.MaxNestingDepth);
                    parameters.Add($"deep_nesting_lines_{i}", m.DeepNestingLines);
                    parameters.Add($"function_count_{i}", m.FunctionCount);
                    parameters.Add($"longest_function_{i}", m.LongestFunction);
                    parameters.Add($"lint_errors_{i}", m.LintErrors);
                    parameters.Add($"lint_warnings_{i}", m.LintWarnings);
                    parameters.Add($"trust_score_{i}", m.TrustScore);
                    parameters.Add($"max_coupling_strength_{i}", m.MaxCouplingStrength);
                    parameters.Add($"coupling_partners_{i}", m.CouplingPartners);
                    parameters.Add($"truck_factor_{i}", m.TruckFactor);
                    parameters.Add($"top_authors_{i}", m.TopAuthors);
                    parameters.Add($"health_score_{i}", m.HealthScore);
                    parameters.Add($"role_{i}", m.Role);
                    parameters.Add($"risk_score_{i}", m.RiskScore);
                    parameters.Add($"computed_at_{i}", m.ComputedAt);
                }

                sb.AppendLine("""
                    ON DUPLICATE KEY UPDATE
                        dotnet_project = VALUES(dotnet_project),
                        changes = VALUES(changes), lines_added = VALUES(lines_added),
                        lines_removed = VALUES(lines_removed), author_count = VALUES(author_count),
                        last_change_at = VALUES(last_change_at),
                        complexity_score = VALUES(complexity_score),
                        max_nesting_depth = VALUES(max_nesting_depth),
                        deep_nesting_lines = VALUES(deep_nesting_lines),
                        function_count = VALUES(function_count),
                        longest_function = VALUES(longest_function),
                        lint_errors = VALUES(lint_errors),
                        lint_warnings = VALUES(lint_warnings),
                        trust_score = VALUES(trust_score),
                        max_coupling_strength = VALUES(max_coupling_strength),
                        coupling_partners = VALUES(coupling_partners),
                        truck_factor = VALUES(truck_factor), top_authors = VALUES(top_authors),
                        health_score = VALUES(health_score), role = VALUES(role),
                        risk_score = VALUES(risk_score), computed_at = VALUES(computed_at)
                    """);

                await conn.ExecuteAsync(sb.ToString(), parameters);
            }
        });

        logger.LogInformation("Upserted {Count} file metrics for {Project}", metrics.Count, project);
    }

    public async Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(
        string project, string? dotnetProject = null)
    {
        await using var conn = await GetOpenConnectionAsync();

        var sql = dotnetProject is null
            ? "SELECT * FROM file_metrics WHERE project = @project"
            : "SELECT * FROM file_metrics WHERE project = @project AND dotnet_project = @dotnetProject";

        var results = await conn.QueryAsync<FileMetricsEntity>(sql,
            new { project, dotnetProject });
        return results.ToList();
    }

    public async Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string project, int top = 10)
    {
        await using var conn = await GetOpenConnectionAsync();

        var results = await conn.QueryAsync<FileMetricsEntity>(
            "SELECT * FROM file_metrics WHERE project = @project ORDER BY risk_score DESC LIMIT @top",
            new { project, top });
        return results.ToList();
    }

    public async Task DeleteFileMetricsAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM file_metrics WHERE project = @project",
            new { project });
    }

    // ── Project Health Summaries ──────────────────────────────────────────

    public async Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary)
    {
        await using var conn = await GetOpenConnectionAsync();

        await conn.ExecuteAsync("""
            INSERT INTO project_health_summaries
                (project, dotnet_project, overall_health, total_files, hotspot_count,
                 alert_count, top_hotspots, computed_at)
            VALUES
                (@Project, COALESCE(@DotnetProject, ''), @OverallHealth, @TotalFiles, @HotspotCount,
                 @AlertCount, @TopHotspots, @ComputedAt)
            ON DUPLICATE KEY UPDATE
                overall_health = VALUES(overall_health),
                total_files = VALUES(total_files),
                hotspot_count = VALUES(hotspot_count),
                alert_count = VALUES(alert_count),
                top_hotspots = VALUES(top_hotspots),
                computed_at = VALUES(computed_at)
            """, summary);
    }

    public async Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();

        var results = await conn.QueryAsync<ProjectHealthSummaryEntity>(
            "SELECT * FROM project_health_summaries WHERE project = @project",
            new { project });
        return results.ToList();
    }

    public async Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync()
    {
        await using var conn = await GetOpenConnectionAsync();

        var results = await conn.QueryAsync<ProjectHealthSummaryEntity>(
            "SELECT * FROM project_health_summaries WHERE dotnet_project = '' ORDER BY overall_health");
        return results.ToList();
    }

    // ── Project Health Analyses (Claude-generated) ────────────────────────

    public async Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis)
    {
        await using var conn = await GetOpenConnectionAsync();

        await conn.ExecuteAsync("""
            INSERT INTO project_health_analyses
                (project, dotnet_project, analysis, confidence, model_used, created_at, updated_at)
            VALUES
                (@Project, COALESCE(@DotnetProject, ''), @Analysis, @Confidence, @ModelUsed, @CreatedAt, @UpdatedAt)
            ON DUPLICATE KEY UPDATE
                analysis = VALUES(analysis),
                confidence = VALUES(confidence),
                model_used = VALUES(model_used),
                updated_at = VALUES(updated_at)
            """, analysis);
    }

    public async Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();

        var results = await conn.QueryAsync<ProjectHealthAnalysisEntity>(
            "SELECT * FROM project_health_analyses WHERE project = @project",
            new { project });
        return results.ToList();
    }
}
