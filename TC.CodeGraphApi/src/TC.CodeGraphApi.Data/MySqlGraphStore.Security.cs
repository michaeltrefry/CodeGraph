using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;

namespace TC.CodeGraphApi.Data;

public partial class MySqlGraphStore
{
    // ── Security Findings ───────────────────────────────────────────────

    public async Task DeleteSecurityFindingsAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM security_findings WHERE project = @project",
            new { project });
    }

    public async Task UpsertSecurityFindingsBatchAsync(string project, IReadOnlyList<SecurityFindingEntity> findings)
    {
        if (findings.Count == 0) return;

        await WithDeadlockRetryAsync(async () =>
        {
            await using var conn = await GetOpenConnectionAsync();

            foreach (var chunk in Chunk(findings, 500))
            {
                var sb = new StringBuilder();
                sb.AppendLine("""
                    INSERT INTO security_findings
                        (project, dotnet_project, category, severity, title, description,
                         file_path, line_number, package, package_version, advisory, computed_at)
                    VALUES
                    """);

                var parameters = new DynamicParameters();
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.AppendLine($"""
                        (@project_{i}, @dotnet_project_{i}, @category_{i}, @severity_{i},
                         @title_{i}, @description_{i}, @file_path_{i}, @line_number_{i},
                         @package_{i}, @package_version_{i}, @advisory_{i}, @computed_at_{i})
                        """);

                    var f = chunk[i];
                    parameters.Add($"project_{i}", project);
                    parameters.Add($"dotnet_project_{i}", f.DotnetProject);
                    parameters.Add($"category_{i}", f.Category);
                    parameters.Add($"severity_{i}", f.Severity);
                    parameters.Add($"title_{i}", f.Title);
                    parameters.Add($"description_{i}", f.Description);
                    parameters.Add($"file_path_{i}", f.FilePath);
                    parameters.Add($"line_number_{i}", f.LineNumber);
                    parameters.Add($"package_{i}", f.Package);
                    parameters.Add($"package_version_{i}", f.PackageVersion);
                    parameters.Add($"advisory_{i}", f.Advisory);
                    parameters.Add($"computed_at_{i}", f.ComputedAt);
                }

                await conn.ExecuteAsync(sb.ToString(), parameters);
            }
        });

        logger.LogInformation("Upserted {Count} security findings for {Project}", findings.Count, project);
    }

    public async Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();

        var results = await conn.QueryAsync<SecurityFindingEntity>(
            "SELECT * FROM security_findings WHERE project = @project ORDER BY FIELD(severity, 'critical', 'high', 'medium', 'low')",
            new { project });
        return results.ToList();
    }

    // ── Project Security Summaries ──────────────────────────────────────

    public async Task UpsertProjectSecuritySummaryAsync(ProjectSecuritySummaryEntity summary)
    {
        await using var conn = await GetOpenConnectionAsync();

        await conn.ExecuteAsync("""
            INSERT INTO project_security_summaries
                (project, security_score, critical_count, high_count, medium_count,
                 low_count, analysis, computed_at)
            VALUES
                (@Project, @SecurityScore, @CriticalCount, @HighCount, @MediumCount,
                 @LowCount, @Analysis, @ComputedAt)
            ON DUPLICATE KEY UPDATE
                security_score = VALUES(security_score),
                critical_count = VALUES(critical_count),
                high_count = VALUES(high_count),
                medium_count = VALUES(medium_count),
                low_count = VALUES(low_count),
                analysis = VALUES(analysis),
                computed_at = VALUES(computed_at)
            """, summary);
    }

    public async Task<ProjectSecuritySummaryEntity?> GetProjectSecuritySummaryAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();

        return await conn.QueryFirstOrDefaultAsync<ProjectSecuritySummaryEntity>(
            "SELECT * FROM project_security_summaries WHERE project = @project",
            new { project });
    }
}
