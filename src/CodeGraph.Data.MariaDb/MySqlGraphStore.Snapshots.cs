using System.Text;
using CodeGraph.Models;
using Dapper;

namespace CodeGraph.Data.MariaDb;

public partial class MySqlGraphStore
{
    public async Task<int> ReplaceProjectGraphAsync(
        string project,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        RepositoryEntity repository,
        SyncStateEntity? syncState,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            var now = DateTime.UtcNow;
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO repositories
                    (name, repo_url, gitlab_group, local_path, default_branch, last_commit_sha,
                     indexed_at, language, framework, is_foundational, properties, created_at, updated_at)
                VALUES
                    (@Name, @RepoUrl, @SourceGroup, @LocalPath, @DefaultBranch, @LastCommitSha,
                     @IndexedAt, @Language, @Framework, @IsFoundational, @Properties, @CreatedAt, @UpdatedAt)
                ON DUPLICATE KEY UPDATE
                    repo_url = COALESCE(VALUES(repo_url), repo_url),
                    gitlab_group = COALESCE(VALUES(gitlab_group), gitlab_group),
                    local_path = COALESCE(VALUES(local_path), local_path),
                    default_branch = COALESCE(VALUES(default_branch), default_branch),
                    last_commit_sha = COALESCE(VALUES(last_commit_sha), last_commit_sha),
                    indexed_at = VALUES(indexed_at),
                    language = COALESCE(VALUES(language), language),
                    framework = COALESCE(VALUES(framework), framework),
                    is_foundational = VALUES(is_foundational),
                    properties = COALESCE(VALUES(properties), properties),
                    updated_at = VALUES(updated_at)
                """, new
            {
                repository.Name,
                repository.RepoUrl,
                repository.SourceGroup,
                repository.LocalPath,
                repository.DefaultBranch,
                repository.LastCommitSha,
                IndexedAt = repository.IndexedAt ?? now,
                repository.Language,
                repository.Framework,
                repository.IsFoundational,
                repository.Properties,
                CreatedAt = repository.CreatedAt == default ? now : repository.CreatedAt,
                UpdatedAt = now
            }, transaction, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition("""
                DELETE na FROM node_analysis na
                INNER JOIN nodes n ON n.id = na.node_id
                WHERE n.project = @Project
                """, new { Project = project }, transaction, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM nodes WHERE project = @Project",
                new { Project = project }, transaction, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM file_hashes WHERE project = @Project",
                new { Project = project }, transaction, cancellationToken: ct));

            var qnToId = new Dictionary<string, long>(nodes.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var batch in nodes.Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder("""
                    INSERT INTO nodes (project, dotnet_project, label, name, qualified_name, file_path, start_line, end_line, properties, do_not_trust)
                    VALUES
                    """);
                var parameters = new DynamicParameters();

                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.AppendLine($"(@Project{i}, @DotnetProject{i}, @Label{i}, @Name{i}, @QualifiedName{i}, @FilePath{i}, @StartLine{i}, @EndLine{i}, @Properties{i}, @DoNotTrust{i})");

                    var node = batch[i];
                    parameters.Add($"Project{i}", node.Project);
                    parameters.Add($"DotnetProject{i}", node.DotnetProject);
                    parameters.Add($"Label{i}", node.Label.ToString());
                    parameters.Add($"Name{i}", Truncate(node.Name, 1000));
                    parameters.Add($"QualifiedName{i}", Truncate(node.QualifiedName, 1000));
                    parameters.Add($"FilePath{i}", node.FilePath);
                    parameters.Add($"StartLine{i}", node.StartLine);
                    parameters.Add($"EndLine{i}", node.EndLine);
                    parameters.Add($"Properties{i}", SerializeJson(node.Properties));
                    parameters.Add($"DoNotTrust{i}", node.DoNotTrust);
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, cancellationToken: ct));

                var storedNames = batch
                    .Select(node => Truncate(node.QualifiedName, 1000))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var rows = await conn.QueryAsync<(long id, string qualified_name)>(new CommandDefinition(
                    "SELECT id, qualified_name FROM nodes WHERE project = @Project AND qualified_name IN @QualifiedNames",
                    new { Project = project, QualifiedNames = storedNames }, transaction, cancellationToken: ct));
                var originalByStoredName = batch
                    .GroupBy(node => Truncate(node.QualifiedName, 1000), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().QualifiedName, StringComparer.OrdinalIgnoreCase);

                foreach (var row in rows)
                {
                    if (originalByStoredName.TryGetValue(row.qualified_name, out var originalName))
                        qnToId[GraphNodeKey.Create(project, originalName)] = row.id;
                }
            }

            var resolvedEdges = ResolveSnapshotEdges(project, edges, qnToId);
            foreach (var batch in resolvedEdges.Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder("""
                    INSERT INTO edges (project, source_id, target_id, type, properties)
                    VALUES
                    """);
                var parameters = new DynamicParameters();

                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.AppendLine($"(@Project{i}, @SourceId{i}, @TargetId{i}, @Type{i}, @Properties{i})");
                    var edge = batch[i];
                    parameters.Add($"Project{i}", edge.Project);
                    parameters.Add($"SourceId{i}", edge.SourceId);
                    parameters.Add($"TargetId{i}", edge.TargetId);
                    parameters.Add($"Type{i}", edge.Type.ToString());
                    parameters.Add($"Properties{i}", SerializeJson(edge.Properties));
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, cancellationToken: ct));
            }

            foreach (var batch in fileHashes.ToArray().Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder("""
                    INSERT INTO file_hashes (project, rel_path, content_hash)
                    VALUES
                    """);
                var parameters = new DynamicParameters();

                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.AppendLine($"(@Project{i}, @RelPath{i}, @ContentHash{i})");
                    parameters.Add($"Project{i}", project);
                    parameters.Add($"RelPath{i}", batch[i].Key);
                    parameters.Add($"ContentHash{i}", batch[i].Value);
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, cancellationToken: ct));
            }

            if (syncState is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO sync_state (project, last_sync_at, last_commit_sha, status, error_message)
                    VALUES (@Project, @LastSyncAt, @LastCommitSha, @Status, @ErrorMessage)
                    ON DUPLICATE KEY UPDATE
                        last_sync_at = VALUES(last_sync_at),
                        last_commit_sha = COALESCE(VALUES(last_commit_sha), last_commit_sha),
                        status = VALUES(status),
                        error_message = VALUES(error_message)
                    """, syncState, transaction, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
            return resolvedEdges.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteFilesFromProjectGraphAsync(
        string project,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return;

        await using var conn = await GetOpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync(ct);
        try
        {
            var pathVariants = ExpandPathVariants(filePaths);
            foreach (var batch in pathVariants.Chunk(1000))
            {
                var paths = batch.ToList();
                await conn.ExecuteAsync(new CommandDefinition("""
                    DELETE na FROM node_analysis na
                    INNER JOIN nodes n ON n.id = na.node_id
                    WHERE n.project = @Project AND n.file_path IN @Paths
                    """, new { Project = project, Paths = paths }, transaction, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM nodes WHERE project = @Project AND file_path IN @Paths",
                    new { Project = project, Paths = paths }, transaction, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM file_hashes WHERE project = @Project AND rel_path IN @Paths",
                    new { Project = project, Paths = paths }, transaction, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static IReadOnlyList<string> ExpandPathVariants(IReadOnlyList<string> filePaths) =>
        filePaths
            .SelectMany(path => new[]
            {
                path.Replace('\\', '/'),
                path.Replace('/', '\\')
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<GraphEdge> ResolveSnapshotEdges(
        string project,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, long> qnToId)
    {
        var resolved = new List<GraphEdge>(edges.Count);
        foreach (var edge in edges)
        {
            if (!qnToId.TryGetValue(GraphNodeKey.Create(project, edge.SourceQN), out var sourceId) ||
                !qnToId.TryGetValue(GraphNodeKey.Create(project, edge.TargetQN), out var targetId))
            {
                continue;
            }

            resolved.Add(new GraphEdge
            {
                Project = project,
                SourceId = sourceId,
                TargetId = targetId,
                Type = edge.Type,
                Properties = edge.Properties ?? []
            });
        }

        return resolved;
    }
}
