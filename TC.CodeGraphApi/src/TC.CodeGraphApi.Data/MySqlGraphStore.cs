using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public partial class MySqlGraphStore(
    CodeGraphDbContext context,
    CodeGraphStorageOptions options,
    ILogger<MySqlGraphStore> logger)
    : IGraphStore, IExclusionStore
{
    private static readonly JsonSerializerOptions JsonOptions = CodeGraphJsonDefaults.CamelCase;

    static MySqlGraphStore()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
    }

    /// Open a fresh pooled connection for Dapper queries.
    /// Each caller must dispose the returned connection (use await using).
    private async Task<MySqlConnection> GetOpenConnectionAsync()
    {
        var conn = new MySqlConnection(options.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// Retry an action on MySQL deadlock (error 1213). Concurrent bulk upserts
    /// from parallel consumers can cause gap-lock contention that resolves on retry.
    /// </summary>
    private async Task WithDeadlockRetryAsync(Func<Task> action, int maxRetries = 3)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (MySqlException ex) when (ex.Number == 1213 && attempt < maxRetries)
            {
                var delay = (attempt + 1) * 200 + Random.Shared.Next(100);
                logger.LogWarning("Deadlock on attempt {Attempt}, retrying in {Delay}ms", attempt + 1, delay);
                await Task.Delay(delay);
            }
        }
    }

    // ── Repositories (EF Core) ──────────────────────────────────────────────

    public async Task UpsertRepositoryAsync(RepositoryEntity repository)
    {
        var existing = await context.Repositories.FindAsync(repository.Name);
        if (existing is null)
        {
            repository.CreatedAt = DateTime.UtcNow;
            repository.UpdatedAt = DateTime.UtcNow;
            repository.IndexedAt = DateTime.UtcNow;
            context.Repositories.Add(repository);
        }
        else
        {
            existing.LocalPath = repository.LocalPath ?? existing.LocalPath;
            existing.RepoUrl = repository.RepoUrl ?? existing.RepoUrl;
            existing.GitLabGroup = repository.GitLabGroup ?? existing.GitLabGroup;
            existing.Language = repository.Language ?? existing.Language;
            existing.Framework = repository.Framework ?? existing.Framework;
            existing.IsFoundational = repository.IsFoundational;
            existing.Properties = repository.Properties ?? existing.Properties;
            existing.IndexedAt = DateTime.UtcNow;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListRepositoriesAsync()
    {
        return await context.Repositories
            .OrderBy(p => p.Name)
            .Select(p => new ProjectInfo(
                p.Name,
                p.RepoUrl,
                p.GitLabGroup,
                p.LocalPath,
                p.LastCommitSha,
                p.IndexedAt,
                p.Language,
                p.Framework,
                p.IsFoundational,
                DeserializeJson(p.Properties)
            ))
            .ToListAsync();
    }
    public async Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25)
    {
        var query = context.Repositories.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(group))
            query = query.Where(p => p.GitLabGroup == group);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => EF.Functions.Like(p.Name, $"%{search}%"));

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProjectInfo(
                p.Name, p.RepoUrl, p.GitLabGroup, p.LocalPath, p.LastCommitSha,
                p.IndexedAt, p.Language, p.Framework, p.IsFoundational,
                DeserializeJson(p.Properties)))
            .ToListAsync();

        return new RepositorySearchResult(items, total);
    }

    public async Task<IReadOnlyList<string>> GetDistinctGroupsAsync()
    {
        return await context.Repositories
            .AsNoTracking()
            .Where(p => p.GitLabGroup != null && p.GitLabGroup != "")
            .Select(p => p.GitLabGroup!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync();
    }

    public async Task<ProjectInfo?> GetRepositoryByName(string name)
    {
        return await context.Repositories
            .Where(p => p.Name == name)
            .Select(p => new ProjectInfo(
                p.Name,
                p.RepoUrl,
                p.GitLabGroup,
                p.LocalPath,
                p.LastCommitSha,
                p.IndexedAt,
                p.Language,
                p.Framework,
                p.IsFoundational,
                DeserializeJson(p.Properties)
            ))
            .FirstOrDefaultAsync();
    }

    public async Task DeleteRepositoryAsync(string project)
    {
        var entity = await context.Repositories.FindAsync(project);
        if (entity is not null)
        {
            context.Repositories.Remove(entity);
            await context.SaveChangesAsync();
        }
    }

    // ── Sync State (EF Core) ──────────────────────────────────────────────

    public async Task<SyncStateEntity?> GetSyncStateAsync(string project)
    {
        return await context.SyncStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Project == project);
    }

    public async Task UpsertSyncStateAsync(SyncStateEntity state)
    {
        var existing = await context.SyncStates.FindAsync(state.Project);
        if (existing is null)
        {
            context.SyncStates.Add(state);
        }
        else
        {
            existing.LastSyncAt = state.LastSyncAt;
            existing.LastCommitSha = state.LastCommitSha ?? existing.LastCommitSha;
            existing.Status = state.Status;
            existing.ErrorMessage = state.ErrorMessage;
        }
        await context.SaveChangesAsync();
    }

    public async Task DeleteSyncStateAsync(string project)
    {
        var existing = await context.SyncStates.FindAsync(project);
        if (existing is not null)
        {
            context.SyncStates.Remove(existing);
            await context.SaveChangesAsync();
        }
    }

    public async Task DeleteAllEdgesForProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM edges WHERE project = @Project",
            new { Project = project });
    }

    public async Task DeleteCrossRepoEdgesForProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM cross_repo_edges WHERE source_project = @Project OR target_project = @Project",
            new { Project = project });
    }

    public async Task DeleteAnalysisDataForProjectAsync(string project)
    {
        await using var conn = await GetOpenConnectionAsync();

        // Delete batch requests for this project's batches
        await conn.ExecuteAsync("""
            DELETE br FROM analysis_batch_requests br
            INNER JOIN analysis_batches b ON br.batch_id = b.id
            WHERE b.repo = @Project
            """, new { Project = project });

        // Delete batches
        await conn.ExecuteAsync(
            "DELETE FROM analysis_batches WHERE repo = @Project",
            new { Project = project });

        // Delete project analyses
        await conn.ExecuteAsync(
            "DELETE FROM project_analyses WHERE repo = @Project",
            new { Project = project });

        // Delete repository summary
        await conn.ExecuteAsync(
            "DELETE FROM repository_summaries WHERE project = @Project",
            new { Project = project });

        // Delete node analyses for nodes in this project (table may not exist)
        try
        {
            await conn.ExecuteAsync("""
                DELETE na FROM node_analyses na
                INNER JOIN nodes n ON na.node_id = n.id
                WHERE n.project = @Project
                """, new { Project = project });
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.NoSuchTable)
        {
            // Table hasn't been created yet — nothing to delete
        }

        // Delete health analyses
        await conn.ExecuteAsync(
            "DELETE FROM project_health_analyses WHERE project = @Project",
            new { Project = project });

        // Delete health summaries
        await conn.ExecuteAsync(
            "DELETE FROM project_health_summaries WHERE project = @Project",
            new { Project = project });
    }

    // ── Shared Helpers ────────────────────────────────────────────────────

    private static GraphNode MapNodeEntity(NodeEntity n) => new()
    {
        Id = n.Id,
        Project = n.Project,
        DotnetProject = n.DotnetProject,
        Label = Enum.Parse<NodeLabel>(n.Label),
        Name = n.Name,
        QualifiedName = n.QualifiedName,
        FilePath = n.FilePath ?? "",
        StartLine = n.StartLine,
        EndLine = n.EndLine,
        Properties = DeserializeJson(n.Properties) ?? new(),
        DoNotTrust = n.DoNotTrust
    };

    private static GraphEdge MapEdgeEntity(EdgeEntity e) => new()
    {
        Id = e.Id,
        Project = e.Project,
        SourceId = e.SourceId,
        TargetId = e.TargetId,
        Type = Enum.Parse<EdgeType>(e.Type),
        Properties = DeserializeJson(e.Properties) ?? new()
    };

    private static GraphNode MapNodeDynamic(dynamic r) => new()
    {
        Id = (long)r.id,
        Project = (string)r.project,
        DotnetProject = (string?)r.dotnet_project,
        Label = Enum.Parse<NodeLabel>((string)r.label),
        Name = (string)r.name,
        QualifiedName = (string)r.qualified_name,
        FilePath = (string)(r.file_path ?? ""),
        StartLine = (int)r.start_line,
        EndLine = (int)r.end_line,
        Properties = DeserializeJson((string?)r.properties) ?? new(),
        DoNotTrust = (bool)(r.do_not_trust ?? false)
    };

    private static string? SerializeJson(Dictionary<string, object>? props)
    {
        if (props is null || props.Count == 0) return null;
        return JsonSerializer.Serialize(props, JsonOptions);
    }

    private static Dictionary<string, object>? DeserializeJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);
    }

    private static T? DeserializeJson<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static List<List<T>> Chunk<T>(IReadOnlyList<T> source, int chunkSize)
    {
        var chunks = new List<List<T>>();
        for (int i = 0; i < source.Count; i += chunkSize)
        {
            var end = Math.Min(i + chunkSize, source.Count);
            var chunk = new List<T>(end - i);
            for (int j = i; j < end; j++)
                chunk.Add(source[j]);
            chunks.Add(chunk);
        }
        return chunks;
    }

    private static List<List<T>> Chunk<T>(IEnumerable<T> source, int chunkSize)
    {
        return Chunk(source.ToList(), chunkSize);
    }
}
