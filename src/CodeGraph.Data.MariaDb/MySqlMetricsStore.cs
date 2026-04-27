using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class MySqlMetricsStore(CodeGraphDbContext db) : IMetricsStore
{
    public async Task UpsertFileMetricsBatchAsync(string project, IReadOnlyList<FileMetricsEntity> metrics)
    {
        if (metrics.Count == 0)
        {
            return;
        }

        foreach (var metric in metrics)
        {
            var existing = await db.FileMetrics
                .FirstOrDefaultAsync(m => m.Project == project && m.FilePath == metric.FilePath);

            metric.Project = project;

            if (existing is null)
            {
                db.FileMetrics.Add(metric);
                continue;
            }

            existing.DotnetProject = metric.DotnetProject;
            existing.Changes = metric.Changes;
            existing.LinesAdded = metric.LinesAdded;
            existing.LinesRemoved = metric.LinesRemoved;
            existing.AuthorCount = metric.AuthorCount;
            existing.LastChangeAt = metric.LastChangeAt;
            existing.ComplexityScore = metric.ComplexityScore;
            existing.MaxNestingDepth = metric.MaxNestingDepth;
            existing.DeepNestingLines = metric.DeepNestingLines;
            existing.FunctionCount = metric.FunctionCount;
            existing.LongestFunction = metric.LongestFunction;
            existing.LintErrors = metric.LintErrors;
            existing.LintWarnings = metric.LintWarnings;
            existing.TrustScore = metric.TrustScore;
            existing.MaxCouplingStrength = metric.MaxCouplingStrength;
            existing.CouplingPartners = metric.CouplingPartners;
            existing.TruckFactor = metric.TruckFactor;
            existing.TopAuthors = metric.TopAuthors;
            existing.HealthScore = metric.HealthScore;
            existing.Role = metric.Role;
            existing.RiskScore = metric.RiskScore;
            existing.Churn30d = metric.Churn30d;
            existing.Churn90d = metric.Churn90d;
            existing.Churn365d = metric.Churn365d;
            existing.BugFixCommits90d = metric.BugFixCommits90d;
            existing.BugFixCommits365d = metric.BugFixCommits365d;
            existing.BugFixRatio365d = metric.BugFixRatio365d;
            existing.RecurringChurnScore = metric.RecurringChurnScore;
            existing.ComputedAt = metric.ComputedAt;
        }

        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(
        string project, string? dotnetProject = null)
    {
        var query = db.FileMetrics.AsNoTracking().Where(m => m.Project == project);

        if (dotnetProject is not null)
        {
            query = query.Where(m => m.DotnetProject == dotnetProject);
        }

        return await query.OrderBy(m => m.FilePath).ToListAsync();
    }

    public async Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string project, int top = 10)
        => await db.FileMetrics.AsNoTracking()
            .Where(m => m.Project == project)
            .OrderByDescending(m => m.RiskScore)
            .ThenByDescending(m => m.HealthScore)
            .Take(top)
            .ToListAsync();

    public async Task DeleteFileMetricsAsync(string project)
    {
        var metrics = await db.FileMetrics.Where(m => m.Project == project).ToListAsync();
        db.FileMetrics.RemoveRange(metrics);
        await db.SaveChangesAsync();
    }

    public async Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary)
    {
        summary.DotnetProject = NormalizeDotnetProject(summary.DotnetProject);

        var existing = await db.ProjectHealthSummaries.FirstOrDefaultAsync(h =>
            h.Project == summary.Project && h.DotnetProject == summary.DotnetProject);

        if (existing is null)
        {
            db.ProjectHealthSummaries.Add(summary);
        }
        else
        {
            existing.OverallHealth = summary.OverallHealth;
            existing.TotalFiles = summary.TotalFiles;
            existing.HotspotCount = summary.HotspotCount;
            existing.AlertCount = summary.AlertCount;
            existing.TopHotspots = summary.TopHotspots;
            existing.ComputedAt = summary.ComputedAt;
        }

        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string project)
        => await db.ProjectHealthSummaries.AsNoTracking()
            .Where(h => h.Project == project)
            .OrderBy(h => h.DotnetProject)
            .ToListAsync();

    public async Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync()
        => await db.ProjectHealthSummaries.AsNoTracking()
            .Where(h => h.DotnetProject == "")
            .OrderBy(h => h.OverallHealth)
            .ToListAsync();

    public async Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis)
    {
        analysis.DotnetProject = NormalizeDotnetProject(analysis.DotnetProject);

        var existing = await db.ProjectHealthAnalyses.FirstOrDefaultAsync(h =>
            h.Project == analysis.Project && h.DotnetProject == analysis.DotnetProject);

        if (existing is null)
        {
            db.ProjectHealthAnalyses.Add(analysis);
        }
        else
        {
            existing.Analysis = analysis.Analysis;
            existing.Confidence = analysis.Confidence;
            existing.ModelUsed = analysis.ModelUsed;
            existing.UpdatedAt = analysis.UpdatedAt;
        }

        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string project)
        => await db.ProjectHealthAnalyses.AsNoTracking()
            .Where(h => h.Project == project)
            .OrderBy(h => h.DotnetProject)
            .ToListAsync();

    public async Task DeleteSecurityFindingsAsync(string project)
    {
        var findings = await db.SecurityFindings.Where(f => f.Project == project).ToListAsync();
        db.SecurityFindings.RemoveRange(findings);
        await db.SaveChangesAsync();
    }

    public async Task UpsertSecurityFindingsBatchAsync(string project, IReadOnlyList<SecurityFindingEntity> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        foreach (var finding in findings)
        {
            finding.Project = project;
        }

        db.SecurityFindings.AddRange(findings);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string project)
        => await db.SecurityFindings.AsNoTracking()
            .Where(f => f.Project == project)
            .OrderBy(f => f.Severity == "critical" ? 0 :
                f.Severity == "high" ? 1 :
                f.Severity == "medium" ? 2 :
                f.Severity == "low" ? 3 : 4)
            .ThenBy(f => f.FilePath)
            .ToListAsync();

    public async Task UpsertProjectSecuritySummaryAsync(ProjectSecuritySummaryEntity summary)
    {
        var existing = await db.ProjectSecuritySummaries.FirstOrDefaultAsync(s => s.Project == summary.Project);

        if (existing is null)
        {
            db.ProjectSecuritySummaries.Add(summary);
        }
        else
        {
            existing.SecurityScore = summary.SecurityScore;
            existing.CriticalCount = summary.CriticalCount;
            existing.HighCount = summary.HighCount;
            existing.MediumCount = summary.MediumCount;
            existing.LowCount = summary.LowCount;
            existing.Analysis = summary.Analysis;
            existing.ComputedAt = summary.ComputedAt;
        }

        await db.SaveChangesAsync();
    }

    public async Task<ProjectSecuritySummaryEntity?> GetProjectSecuritySummaryAsync(string project)
        => await db.ProjectSecuritySummaries.AsNoTracking().FirstOrDefaultAsync(s => s.Project == project);

    private static string NormalizeDotnetProject(string? dotnetProject)
        => string.IsNullOrWhiteSpace(dotnetProject) ? "" : dotnetProject;
}
