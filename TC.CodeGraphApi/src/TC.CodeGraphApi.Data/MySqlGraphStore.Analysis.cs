using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public partial class MySqlGraphStore
{
    // ── Summaries (EF Core) ───────────────────────────────────────────────

    public async Task UpsertRepositorySummaryAsync(string project, string summary,
        ConfidenceLevel confidence, string sourceHash, string? modelUsed = null)
    {
        var existing = await context.RepositorySummaries.FindAsync(project);
        if (existing is null)
        {
            context.RepositorySummaries.Add(new RepositorySummaryEntity
            {
                Project = project,
                Summary = summary,
                Confidence = confidence.ToString().ToLowerInvariant(),
                SourceHash = sourceHash,
                ModelUsed = modelUsed,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Summary = summary;
            existing.Confidence = confidence.ToString().ToLowerInvariant();
            existing.SourceHash = sourceHash;
            existing.ModelUsed = modelUsed;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
    }

    public async Task<ProjectSummary?> GetRepositorySummaryAsync(string project)
    {
        var entity = await context.RepositorySummaries
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Project == project);

        if (entity is null) return null;

        return new ProjectSummary(
            entity.Project,
            entity.Summary,
            Enum.Parse<ConfidenceLevel>(entity.Confidence, ignoreCase: true),
            entity.SourceHash,
            entity.ModelUsed,
            entity.CreatedAt,
            entity.UpdatedAt
        );
    }

    // ── Per-project analyses (EF Core) ───────────────────────────────────

    public async Task UpsertProjectAnalysisAsync(string repo, StoredProjectAnalysis analysis)
    {
        var existing = await context.ProjectAnalyses
            .FirstOrDefaultAsync(a => a.Repo == repo && a.ProjectName == analysis.ProjectName);

        var endpointsJson = JsonSerializer.Serialize(analysis.Endpoints, JsonOptions);
        var servicesJson = JsonSerializer.Serialize(analysis.Services, JsonOptions);
        var depsJson = JsonSerializer.Serialize(analysis.ExternalDependencies, JsonOptions);
        var tablesJson = JsonSerializer.Serialize(analysis.DatabaseTables, JsonOptions);

        if (existing is null)
        {
            context.ProjectAnalyses.Add(new ProjectAnalysisEntity
            {
                Repo = repo,
                ProjectName = analysis.ProjectName,
                Summary = analysis.Summary,
                Confidence = analysis.Confidence.ToString().ToLowerInvariant(),
                Endpoints = endpointsJson,
                Services = servicesJson,
                ExternalDependencies = depsJson,
                DatabaseTables = tablesJson,
                ModelUsed = analysis.ModelUsed,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Summary = analysis.Summary;
            existing.Confidence = analysis.Confidence.ToString().ToLowerInvariant();
            existing.Endpoints = endpointsJson;
            existing.Services = servicesJson;
            existing.ExternalDependencies = depsJson;
            existing.DatabaseTables = tablesJson;
            existing.ModelUsed = analysis.ModelUsed;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<StoredProjectAnalysis>> GetProjectAnalysesAsync(string repo)
    {
        var entities = await context.ProjectAnalyses
            .AsNoTracking()
            .Where(a => a.Repo == repo)
            .ToListAsync();

        return entities.Select(e => new StoredProjectAnalysis(
            e.Repo,
            e.ProjectName,
            e.Summary,
            Enum.Parse<ConfidenceLevel>(e.Confidence, ignoreCase: true),
            DeserializeJson<List<StoredEndpoint>>(e.Endpoints) ?? [],
            DeserializeJson<List<StoredService>>(e.Services) ?? [],
            DeserializeJson<List<string>>(e.ExternalDependencies) ?? [],
            DeserializeJson<List<string>>(e.DatabaseTables) ?? [],
            e.ModelUsed,
            e.UpdatedAt
        )).ToList();
    }

    // ── Graph Context for Batch Analysis (Dapper) ────────────────────────

    public async Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        var labels = new[] { NodeLabel.Class, NodeLabel.Interface }
            .Select(l => l.ToString())
            .ToList();

        var rows = await conn.QueryAsync<NodeEntity>("""
            SELECT DISTINCT n.id, n.project, n.dotnet_project, n.label, n.name, n.qualified_name,
                   n.file_path, n.start_line, n.end_line, n.properties
            FROM nodes n
            WHERE n.project = @Project
              AND n.label IN @Labels
              AND (
                  EXISTS (SELECT 1 FROM edges e WHERE e.source_id = n.id)
               OR EXISTS (SELECT 1 FROM edges e WHERE e.target_id = n.id)
              )
            ORDER BY n.name
            """,
            new { Project = project, Labels = labels });

        return rows.ToList();
    }

    public async Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<NodeEntity>("""
            SELECT n.id, n.project, n.dotnet_project, n.label, n.name, n.qualified_name,
                   n.file_path, n.start_line, n.end_line, n.properties
            FROM edges e
            JOIN nodes n ON n.id = e.target_id
            WHERE e.source_id = @ParentNodeId
              AND e.type = 'DEFINES'
            ORDER BY n.label, n.name
            """,
            new { ParentNodeId = parentNodeId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetOutboundEdgesAsync(long nodeId)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<EdgeEntity>("""
            SELECT id, project, source_id, target_id, type, properties
            FROM edges
            WHERE source_id = @NodeId
              AND type NOT IN ('DEFINES', 'CONTAINS_FILE', 'CONTAINS_FOLDER', 'CONTAINS_NAMESPACE')
            ORDER BY type
            """,
            new { NodeId = nodeId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<EdgeEntity>("""
            SELECT id, project, source_id, target_id, type, properties
            FROM edges
            WHERE target_id = @NodeId
              AND type NOT IN ('DEFINES', 'CONTAINS_FILE', 'CONTAINS_FOLDER', 'CONTAINS_NAMESPACE')
            ORDER BY type
            """,
            new { NodeId = nodeId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<NodeEntity>> GetAllNodesByProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<NodeEntity>("""
            SELECT id, project, dotnet_project, label, name, qualified_name,
                   file_path, start_line, end_line, properties, do_not_trust
            FROM nodes
            WHERE project = @Project
            ORDER BY label, name
            """,
            new { Project = project });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetAllEdgesByProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<EdgeEntity>("""
            SELECT id, project, source_id, target_id, type, properties
            FROM edges
            WHERE project = @Project
            """,
            new { Project = project });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<EdgeEntity>> GetEdgesForNodesAsync(IReadOnlyList<long> nodeIds)
    {
        if (nodeIds.Count == 0) return [];
        await using var conn = await GetOpenConnectionAsync();
        var rows = await conn.QueryAsync<EdgeEntity>("""
            SELECT id, project, source_id, target_id, type, properties
            FROM edges
            WHERE source_id IN @NodeIds
               OR target_id IN @NodeIds
            """,
            new { NodeIds = nodeIds });
        return rows.ToList();
    }

    // ── Analysis Batch Tracking (EF Core) ────────────────────────────────

    public async Task<long> CreateAnalysisBatchAsync(AnalysisBatchEntity batch)
    {
        context.AnalysisBatches.Add(batch);
        await context.SaveChangesAsync();
        return batch.Id;
    }

    public async Task CreateBatchRequestsAsync(IEnumerable<AnalysisBatchRequestEntity> requests)
    {
        context.AnalysisBatchRequests.AddRange(requests);
        await context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<StoredAnalysisBatch>> GetPendingBatchesAsync(string? repo = null)
    {
        var query = context.AnalysisBatches
            .AsNoTracking()
            .Where(b => b.Status == "submitted");

        if (repo is not null)
            query = query.Where(b => b.Repo == repo);

        var entities = await query.OrderBy(b => b.SubmittedAt).ToListAsync();

        return entities.Select(e => new StoredAnalysisBatch(
            e.Id, e.Repo, e.AnthropicBatchId, e.Status,
            e.RequestCount, e.CompletedCount, e.SubmittedAt, e.CompletedAt
        )).ToList();
    }

    public async Task<StoredAnalysisBatch?> GetLatestBatchAsync(string repo)
    {
        var entity = await context.AnalysisBatches
            .AsNoTracking()
            .Where(b => b.Repo == repo)
            .OrderByDescending(b => b.SubmittedAt)
            .FirstOrDefaultAsync();

        return entity is null
            ? null
            : new StoredAnalysisBatch(
                entity.Id, entity.Repo, entity.AnthropicBatchId, entity.Status,
                entity.RequestCount, entity.CompletedCount, entity.SubmittedAt, entity.CompletedAt);
    }

    public async Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt)
    {
        var entity = await context.AnalysisBatches.FindAsync(batchId);
        if (entity is null) return;
        entity.Status = status;
        entity.CompletedCount = completedCount;
        entity.CompletedAt = completedAt;
        await context.SaveChangesAsync();
    }

    public async Task UpdateBatchRequestStatusAsync(string customId, string status, DateTime completedAt)
    {
        var entity = await context.AnalysisBatchRequests
            .FirstOrDefaultAsync(r => r.CustomId == customId);
        if (entity is null) return;
        entity.Status = status;
        entity.CompletedAt = completedAt;
        await context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AnalysisBatchRequestEntity>> GetBatchRequestsAsync(long batchId)
    {
        return await context.AnalysisBatchRequests
            .Where(r => r.BatchId == batchId)
            .ToListAsync();
    }

    // ── Node Analysis Results (EF Core) ───────────────────────────────────

    public async Task UpsertNodeAnalysisAsync(NodeAnalysisEntity analysis)
    {
        var existing = await context.NodeAnalyses.FindAsync(analysis.NodeId);
        if (existing is null)
        {
            analysis.CreatedAt = DateTime.UtcNow;
            analysis.UpdatedAt = DateTime.UtcNow;
            context.NodeAnalyses.Add(analysis);
        }
        else
        {
            existing.Description = analysis.Description;
            existing.Confidence = analysis.Confidence;
            existing.ModelUsed = analysis.ModelUsed;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
    }

    public async Task<StoredNodeAnalysis?> GetNodeAnalysisAsync(long nodeId)
    {
        var entity = await context.NodeAnalyses
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.NodeId == nodeId);

        if (entity is null) return null;

        return new StoredNodeAnalysis(
            entity.NodeId, entity.Description, entity.Confidence,
            entity.ModelUsed, entity.CreatedAt, entity.UpdatedAt);
    }

    public async Task<Dictionary<long, StoredNodeAnalysis>> GetNodeAnalysesBatchAsync(IReadOnlyList<long> nodeIds)
    {
        if (nodeIds.Count == 0) return new Dictionary<long, StoredNodeAnalysis>();

        var entities = await context.NodeAnalyses
            .AsNoTracking()
            .Where(n => nodeIds.Contains(n.NodeId))
            .ToListAsync();

        return entities.ToDictionary(
            e => e.NodeId,
            e => new StoredNodeAnalysis(e.NodeId, e.Description, e.Confidence, e.ModelUsed, e.CreatedAt, e.UpdatedAt));
    }
}
