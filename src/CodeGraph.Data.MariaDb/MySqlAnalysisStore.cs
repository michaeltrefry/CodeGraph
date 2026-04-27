using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class MySqlAnalysisStore(CodeGraphDbContext db) : IAnalysisStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task UpsertRepositorySummaryAsync(string project, string summary,
        ConfidenceLevel confidence, string sourceHash, string? modelUsed = null)
    {
        var existing = await db.RepositorySummaries.FindAsync(project);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            db.RepositorySummaries.Add(new RepositorySummaryEntity
            {
                Project = project,
                Summary = summary,
                Confidence = confidence.ToString().ToLowerInvariant(),
                SourceHash = sourceHash,
                ModelUsed = modelUsed,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Summary = summary;
            existing.Confidence = confidence.ToString().ToLowerInvariant();
            existing.SourceHash = sourceHash;
            existing.ModelUsed = modelUsed;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task<ProjectSummary?> GetRepositorySummaryAsync(string project)
    {
        var entity = await db.RepositorySummaries.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Project == project);

        return entity is null
            ? null
            : new ProjectSummary(
                entity.Project,
                entity.Summary,
                Enum.Parse<ConfidenceLevel>(entity.Confidence, ignoreCase: true),
                entity.SourceHash,
                entity.ModelUsed,
                entity.CreatedAt,
                entity.UpdatedAt);
    }

    public async Task UpsertProjectAnalysisAsync(string repo, StoredProjectAnalysis analysis)
    {
        var existing = await db.ProjectAnalyses
            .FirstOrDefaultAsync(a => a.Repo == repo && a.ProjectName == analysis.ProjectName);

        var now = DateTime.UtcNow;
        var endpoints = SerializeJson(analysis.Endpoints);
        var services = SerializeJson(analysis.Services);
        var externalDependencies = SerializeJson(analysis.ExternalDependencies);
        var databaseTables = SerializeJson(analysis.DatabaseTables);

        if (existing is null)
        {
            db.ProjectAnalyses.Add(new ProjectAnalysisEntity
            {
                Repo = repo,
                ProjectName = analysis.ProjectName,
                Summary = analysis.Summary,
                Confidence = analysis.Confidence.ToString().ToLowerInvariant(),
                Endpoints = endpoints,
                Services = services,
                ExternalDependencies = externalDependencies,
                DatabaseTables = databaseTables,
                ModelUsed = analysis.ModelUsed,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Summary = analysis.Summary;
            existing.Confidence = analysis.Confidence.ToString().ToLowerInvariant();
            existing.Endpoints = endpoints;
            existing.Services = services;
            existing.ExternalDependencies = externalDependencies;
            existing.DatabaseTables = databaseTables;
            existing.ModelUsed = analysis.ModelUsed;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<StoredProjectAnalysis>> GetProjectAnalysesAsync(string repo)
    {
        var entities = await db.ProjectAnalyses.AsNoTracking()
            .Where(a => a.Repo == repo)
            .OrderBy(a => a.ProjectName)
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
            e.UpdatedAt)).ToList();
    }

    public async Task<long> CreateAnalysisBatchAsync(AnalysisBatchEntity batch)
    {
        db.AnalysisBatches.Add(batch);
        await db.SaveChangesAsync();
        return batch.Id;
    }

    public async Task CreateBatchRequestsAsync(IEnumerable<AnalysisBatchRequestEntity> requests)
    {
        var requestList = requests.ToList();
        if (requestList.Count == 0)
        {
            return;
        }

        db.AnalysisBatchRequests.AddRange(requestList);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<StoredAnalysisBatch>> GetPendingBatchesAsync(string? repo = null)
    {
        var query = db.AnalysisBatches.AsNoTracking().Where(b => b.Status == "submitted");

        if (repo is not null)
        {
            query = query.Where(b => b.Repo == repo);
        }

        var batches = await query.OrderBy(b => b.SubmittedAt).ToListAsync();
        return batches.Select(MapBatch).ToList();
    }

    public async Task<StoredAnalysisBatch?> GetLatestBatchAsync(string repo)
    {
        var batch = await db.AnalysisBatches.AsNoTracking()
            .Where(b => b.Repo == repo)
            .OrderByDescending(b => b.SubmittedAt)
            .FirstOrDefaultAsync();

        return batch is null ? null : MapBatch(batch);
    }

    public async Task<StoredAnalysisBatch?> GetBatchByProviderBatchIdAsync(string providerBatchId)
    {
        var batch = await db.AnalysisBatches.AsNoTracking()
            .Where(b => b.ProviderBatchId == providerBatchId)
            .OrderByDescending(b => b.SubmittedAt)
            .FirstOrDefaultAsync();

        return batch is null ? null : MapBatch(batch);
    }

    public async Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt)
    {
        var batch = await db.AnalysisBatches.FindAsync(batchId);
        if (batch is null)
        {
            return;
        }

        batch.Status = status;
        batch.CompletedCount = completedCount;
        batch.CompletedAt = completedAt;
        await db.SaveChangesAsync();
    }

    public async Task UpdateBatchRequestStateAsync(long batchId, string customId, string status, int attemptCount,
        string? responseText, string? modelUsed, DateTime? completedAt)
    {
        var request = await db.AnalysisBatchRequests.FirstOrDefaultAsync(r =>
            r.BatchId == batchId && r.CustomId == customId);

        if (request is null)
        {
            return;
        }

        request.Status = status;
        request.AttemptCount = attemptCount;
        request.ResponseText = responseText;
        request.ModelUsed = modelUsed;
        request.CompletedAt = completedAt;
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AnalysisBatchRequestEntity>> GetBatchRequestsAsync(long batchId)
        => await db.AnalysisBatchRequests.AsNoTracking()
            .Where(r => r.BatchId == batchId)
            .OrderBy(r => r.Sequence)
            .ThenBy(r => r.CustomId)
            .ToListAsync();

    public async Task UpsertNodeAnalysisAsync(NodeAnalysisEntity analysis)
    {
        var existing = await db.NodeAnalyses.FindAsync(analysis.NodeId);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            if (analysis.CreatedAt == default)
            {
                analysis.CreatedAt = now;
            }

            if (analysis.UpdatedAt == default)
            {
                analysis.UpdatedAt = now;
            }

            db.NodeAnalyses.Add(analysis);
        }
        else
        {
            existing.Description = analysis.Description;
            existing.Confidence = analysis.Confidence;
            existing.ModelUsed = analysis.ModelUsed;
            existing.UpdatedAt = analysis.UpdatedAt == default ? now : analysis.UpdatedAt;
        }

        await db.SaveChangesAsync();
    }

    public async Task<StoredNodeAnalysis?> GetNodeAnalysisAsync(long nodeId)
    {
        var entity = await db.NodeAnalyses.AsNoTracking()
            .FirstOrDefaultAsync(n => n.NodeId == nodeId);

        return entity is null ? null : MapNodeAnalysis(entity);
    }

    public async Task<Dictionary<long, StoredNodeAnalysis>> GetNodeAnalysesBatchAsync(IReadOnlyList<long> nodeIds)
    {
        if (nodeIds.Count == 0)
        {
            return [];
        }

        var entities = await db.NodeAnalyses.AsNoTracking()
            .Where(n => nodeIds.Contains(n.NodeId))
            .ToListAsync();

        return entities.ToDictionary(e => e.NodeId, MapNodeAnalysis);
    }

    public async Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string project)
    {
        var classLabel = NodeLabel.Class.ToString();
        var interfaceLabel = NodeLabel.Interface.ToString();

        return await db.Nodes.AsNoTracking()
            .Where(n => n.Project == project
                && (n.Label == classLabel || n.Label == interfaceLabel)
                && (db.Edges.Any(e => e.SourceId == n.Id) || db.Edges.Any(e => e.TargetId == n.Id)))
            .OrderBy(n => n.Name)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId)
        => await db.Edges.AsNoTracking()
            .Where(e => e.SourceId == parentNodeId && (e.Type == "DEFINES" || e.Type == "DEFINES_METHOD"))
            .Join(db.Nodes.AsNoTracking(),
                e => e.TargetId,
                n => n.Id,
                (_, n) => n)
            .OrderBy(n => n.Label)
            .ThenBy(n => n.Name)
            .ToListAsync();

    public async Task<IReadOnlyList<EdgeEntity>> GetOutboundEdgesAsync(long nodeId)
        => await db.Edges.AsNoTracking()
            .Where(e => e.SourceId == nodeId
                && e.Type != "DEFINES"
                && e.Type != "DEFINES_METHOD"
                && e.Type != "CONTAINS_FILE"
                && e.Type != "CONTAINS_FOLDER"
                && e.Type != "CONTAINS_NAMESPACE"
                && e.Type != "CONTAINS_PROJECT")
            .OrderBy(e => e.Type)
            .ToListAsync();

    public async Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId)
        => await db.Edges.AsNoTracking()
            .Where(e => e.TargetId == nodeId
                && e.Type != "DEFINES"
                && e.Type != "DEFINES_METHOD"
                && e.Type != "CONTAINS_FILE"
                && e.Type != "CONTAINS_FOLDER"
                && e.Type != "CONTAINS_NAMESPACE"
                && e.Type != "CONTAINS_PROJECT")
            .OrderBy(e => e.Type)
            .ToListAsync();

    public async Task<IReadOnlyList<NodeEntity>> GetAllNodesByProjectAsync(string project)
        => await db.Nodes.AsNoTracking()
            .Where(n => n.Project == project)
            .OrderBy(n => n.Label)
            .ThenBy(n => n.Name)
            .ToListAsync();

    public async Task<IReadOnlyList<EdgeEntity>> GetAllEdgesByProjectAsync(string project)
        => await db.Edges.AsNoTracking()
            .Where(e => e.Project == project)
            .OrderBy(e => e.Id)
            .ToListAsync();

    public async Task<IReadOnlyList<EdgeEntity>> GetEdgesForNodesAsync(IReadOnlyList<long> nodeIds)
    {
        if (nodeIds.Count == 0)
        {
            return [];
        }

        return await db.Edges.AsNoTracking()
            .Where(e => nodeIds.Contains(e.SourceId) || nodeIds.Contains(e.TargetId))
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    private static StoredAnalysisBatch MapBatch(AnalysisBatchEntity entity)
        => new(entity.Id, entity.Repo, entity.ProviderBatchId, entity.ProviderName, entity.ExecutionMode,
            entity.IncludeAllSource, entity.Status, entity.RequestCount, entity.CompletedCount, entity.SubmittedAt,
            entity.CompletedAt);

    private static StoredNodeAnalysis MapNodeAnalysis(NodeAnalysisEntity entity)
        => new(entity.NodeId, entity.Description, entity.Confidence, entity.ModelUsed, entity.CreatedAt,
            entity.UpdatedAt);

    private static string SerializeJson<T>(T value)
        => JsonSerializer.Serialize(value, JsonOptions);

    private static T? DeserializeJson<T>(string? json)
        => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);

}
