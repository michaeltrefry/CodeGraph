using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class MySqlReviewStore(CodeGraphDbContext db) : IReviewStore
{
    private static readonly string[] StartedStatuses = ["running", "completed", "failed"];

    public async Task DeleteProjectDiagnosticsAsync(string project)
    {
        await db.ProjectDiagnostics
            .Where(d => d.Project == project)
            .ExecuteDeleteAsync();
    }

    public async Task UpsertProjectDiagnosticsBatchAsync(string project, IReadOnlyList<ProjectDiagnosticEntity> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        foreach (var diagnostic in diagnostics)
        {
            diagnostic.Project = project;
            diagnostic.DiagnosticKey = ProjectDiagnosticKey.EnsureWithinLimit(diagnostic.DiagnosticKey);
        }

        var keys = diagnostics
            .Select(d => d.DiagnosticKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count > 0)
        {
            await db.ProjectDiagnostics
                .Where(d => d.Project == project && keys.Contains(d.DiagnosticKey))
                .ExecuteDeleteAsync();
            DetachTrackedProjectDiagnostics(project, keys);
        }

        db.ProjectDiagnostics.AddRange(diagnostics);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(
        string project, string? dotnetProject = null)
    {
        var query = db.ProjectDiagnostics.AsNoTracking()
            .Where(d => d.Project == project);

        if (dotnetProject is not null)
        {
            query = query.Where(d => d.DotnetProject == dotnetProject);
        }

        return await query
            .OrderBy(d => d.Severity == "error" ? 0 :
                d.Severity == "warning" ? 1 :
                d.Severity == "info" ? 2 :
                d.Severity == "suggestion" ? 3 : 4)
            .ThenBy(d => d.FilePath)
            .ThenBy(d => d.LineStart ?? 0)
            .ThenBy(d => d.DiagnosticId)
            .ToListAsync();
    }

    public async Task<long> CreateProjectReviewRunAsync(ProjectReviewRunEntity run)
    {
        if (run.CreatedAt == default)
        {
            run.CreatedAt = DateTime.UtcNow;
        }

        db.ProjectReviewRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    public async Task UpdateProjectReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null,
        DateTime? completedAt = null, string? error = null)
    {
        var run = await db.ProjectReviewRuns.FindAsync(reviewRunId);
        if (run is null)
        {
            return;
        }

        run.Status = status;
        if (overviewJson is not null)
        {
            run.OverviewJson = overviewJson;
        }

        if (completedAt is not null)
        {
            run.CompletedAt = completedAt;
        }

        if (StartedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            run.StartedAt ??= DateTime.UtcNow;
        }

        run.Error = error;
        await db.SaveChangesAsync();
    }

    public async Task UpsertProjectReviewFindingsAsync(long reviewRunId, IReadOnlyList<ProjectReviewFindingEntity> findings)
    {
        await db.ProjectReviewFindings
            .Where(f => f.ReviewRunId == reviewRunId)
            .ExecuteDeleteAsync();
        DetachTrackedEntities<ProjectReviewFindingEntity>(reviewRunId);

        if (findings.Count == 0)
        {
            return;
        }

        foreach (var finding in findings)
        {
            finding.Id = 0;
            finding.ReviewRunId = reviewRunId;
        }

        db.ProjectReviewFindings.AddRange(findings);
        await db.SaveChangesAsync();
    }

    public async Task<ProjectReviewRunEntity?> GetProjectReviewRunAsync(long reviewRunId)
        => await db.ProjectReviewRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reviewRunId);

    public async Task<ProjectReviewRunEntity?> GetLatestProjectReviewRunAsync(string project, string projectName)
        => await db.ProjectReviewRuns.AsNoTracking()
            .Where(r => r.Project == project && r.ProjectName == projectName)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<ProjectReviewFindingEntity>> GetProjectReviewFindingsAsync(long reviewRunId)
        => await db.ProjectReviewFindings.AsNoTracking()
            .Where(f => f.ReviewRunId == reviewRunId)
            .OrderBy(f => f.Ordinal)
            .ThenBy(f => f.Id)
            .ToListAsync();

    public async Task<long> CreateRepositoryReviewRunAsync(RepositoryReviewRunEntity run)
    {
        if (run.CreatedAt == default)
        {
            run.CreatedAt = DateTime.UtcNow;
        }

        db.RepositoryReviewRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    public async Task UpdateRepositoryReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null,
        DateTime? completedAt = null, string? error = null)
    {
        var run = await db.RepositoryReviewRuns.FindAsync(reviewRunId);
        if (run is null)
        {
            return;
        }

        run.Status = status;
        if (overviewJson is not null)
        {
            run.OverviewJson = overviewJson;
        }

        if (completedAt is not null)
        {
            run.CompletedAt = completedAt;
        }

        if (StartedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            run.StartedAt ??= DateTime.UtcNow;
        }

        run.Error = error;
        await db.SaveChangesAsync();
    }

    public async Task UpsertRepositoryReviewFindingsAsync(long reviewRunId,
        IReadOnlyList<RepositoryReviewFindingEntity> findings)
    {
        await db.RepositoryReviewFindings
            .Where(f => f.ReviewRunId == reviewRunId)
            .ExecuteDeleteAsync();
        DetachTrackedEntities<RepositoryReviewFindingEntity>(reviewRunId);

        if (findings.Count == 0)
        {
            return;
        }

        foreach (var finding in findings)
        {
            finding.Id = 0;
            finding.ReviewRunId = reviewRunId;
        }

        db.RepositoryReviewFindings.AddRange(findings);
        await db.SaveChangesAsync();
    }

    public async Task UpsertRepositoryReviewProjectSectionsAsync(long reviewRunId,
        IReadOnlyList<RepositoryReviewProjectSectionEntity> sections)
    {
        await db.RepositoryReviewProjectSections
            .Where(s => s.ReviewRunId == reviewRunId)
            .ExecuteDeleteAsync();
        DetachTrackedEntities<RepositoryReviewProjectSectionEntity>(reviewRunId);

        if (sections.Count == 0)
        {
            return;
        }

        foreach (var section in sections)
        {
            section.Id = 0;
            section.ReviewRunId = reviewRunId;
        }

        db.RepositoryReviewProjectSections.AddRange(sections);
        await db.SaveChangesAsync();
    }

    public async Task<RepositoryReviewRunEntity?> GetRepositoryReviewRunAsync(long reviewRunId)
        => await db.RepositoryReviewRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reviewRunId);

    public async Task<RepositoryReviewRunEntity?> GetLatestRepositoryReviewRunAsync(string repo)
        => await db.RepositoryReviewRuns.AsNoTracking()
            .Where(r => r.Repo == repo)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<RepositoryReviewRunEntity>> GetRepositoryReviewRunsByStatusAsync(
        IReadOnlyList<string> statuses)
    {
        var normalizedStatuses = statuses
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .Select(status => status.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedStatuses.Count == 0)
        {
            return [];
        }

        return await db.RepositoryReviewRuns.AsNoTracking()
            .Where(r => normalizedStatuses.Contains(r.Status.ToLower()))
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<RepositoryReviewFindingEntity>> GetRepositoryReviewFindingsAsync(long reviewRunId)
        => await db.RepositoryReviewFindings.AsNoTracking()
            .Where(f => f.ReviewRunId == reviewRunId)
            .OrderBy(f => f.Ordinal)
            .ThenBy(f => f.Id)
            .ToListAsync();

    public async Task<IReadOnlyList<RepositoryReviewProjectSectionEntity>> GetRepositoryReviewProjectSectionsAsync(
        long reviewRunId)
        => await db.RepositoryReviewProjectSections.AsNoTracking()
            .Where(s => s.ReviewRunId == reviewRunId)
            .OrderBy(s => s.ProjectName)
            .ThenBy(s => s.Id)
            .ToListAsync();

    private void DetachTrackedProjectDiagnostics(string project, IReadOnlyCollection<string> diagnosticKeys)
    {
        var entries = db.ChangeTracker.Entries<ProjectDiagnosticEntity>()
            .Where(e => e.Entity.Project == project && diagnosticKeys.Contains(e.Entity.DiagnosticKey))
            .ToList();

        foreach (var entry in entries)
        {
            entry.State = EntityState.Detached;
        }
    }

    private void DetachTrackedEntities<TEntity>(long reviewRunId)
        where TEntity : class
    {
        var entries = db.ChangeTracker.Entries<TEntity>()
            .Where(e => e.Entity switch
            {
                ProjectReviewFindingEntity finding => finding.ReviewRunId == reviewRunId,
                RepositoryReviewFindingEntity finding => finding.ReviewRunId == reviewRunId,
                RepositoryReviewProjectSectionEntity section => section.ReviewRunId == reviewRunId,
                _ => false
            })
            .ToList();

        foreach (var entry in entries)
        {
            entry.State = EntityState.Detached;
        }
    }
}
