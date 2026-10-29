using System.Text;
using CodeGraph.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public partial class MySqlGraphStore
{
    public async Task<int> ReplaceRepositoryFilesAsync(
        string repository,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        GraphSliceRetractions? retractions = null,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return 0;

        await using var conn = await GetOpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync(ct);
        try
        {
            var pathVariants = ExpandPathVariants(filePaths);
            // Retracted edges go first, so they are never preserved below.
            if (retractions is { Edges.Count: > 0 })
                await DeleteRetractedEdgesAsync(conn, transaction, repository, retractions.Edges, ct);

            var preservedIncoming = (await conn.QueryAsync<StoredPendingEdge>(new CommandDefinition("""
                SELECT source.qualified_name AS SourceQN,
                       target.qualified_name AS TargetQN,
                       edge.type AS Type,
                       edge.properties AS Properties
                FROM edges edge
                INNER JOIN nodes source ON source.id = edge.source_id
                INNER JOIN nodes target ON target.id = edge.target_id
                WHERE target.repository = @Repository
                  AND target.file_path IN @Paths
                  AND NOT (source.repository = @Repository AND source.file_path IN @Paths)
                """, new { Repository = repository, Paths = pathVariants }, transaction,
                commandTimeout: GraphWriteTimeout, cancellationToken: ct))).ToList();

            var replacedNodeIds = (await conn.QueryAsync<long>(new CommandDefinition(
                "SELECT id FROM nodes WHERE repository = @Repository AND file_path IN @Paths",
                new { Repository = repository, Paths = pathVariants }, transaction,
                commandTimeout: GraphWriteTimeout, cancellationToken: ct))).ToList();
            await DeleteNodesInBatchesAsync(conn, transaction, replacedNodeIds, ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM file_hashes WHERE repository = @Repository AND rel_path IN @Paths",
                new { Repository = repository, Paths = pathVariants }, transaction,
                commandTimeout: GraphWriteTimeout, cancellationToken: ct));

            var candidates = nodes
                .GroupBy(node => Truncate(node.QualifiedName, 1000), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var endpointNames = candidates.Select(node => Truncate(node.QualifiedName, 1000))
                .Concat(edges.SelectMany(edge => new[]
                {
                    Truncate(edge.SourceQN, 1000),
                    Truncate(edge.TargetQN, 1000)
                }))
                .Concat(preservedIncoming.SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN }))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var qnToId = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var batch in endpointNames.Chunk(1000))
            {
                var lookup = new DynamicParameters(new { Repository = repository });
                var rows = await conn.QueryAsync<(long Id, string QualifiedName)>(new CommandDefinition(
                    $"SELECT id AS Id, qualified_name AS QualifiedName FROM nodes WHERE repository = @Repository AND {QualifiedNameMatch(batch, lookup)}",
                    lookup, transaction,
                    commandTimeout: GraphWriteTimeout, cancellationToken: ct));
                foreach (var row in rows)
                    qnToId[row.QualifiedName] = row.Id;
            }

            var nodesToInsert = candidates
                .Where(node => !qnToId.ContainsKey(Truncate(node.QualifiedName, 1000)))
                .ToList();
            foreach (var batch in nodesToInsert.Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder("""
                    INSERT INTO nodes (repository, dotnet_project, label, name, qualified_name, file_path, start_line, end_line, properties, do_not_trust)
                    VALUES
                    """);
                var parameters = new DynamicParameters();

                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.AppendLine($"(@Repository{i}, @DotnetProject{i}, @Label{i}, @Name{i}, @QualifiedName{i}, @FilePath{i}, @StartLine{i}, @EndLine{i}, @Properties{i}, @DoNotTrust{i})");
                    var node = batch[i];
                    parameters.Add($"Repository{i}", repository);
                    parameters.Add($"DotnetProject{i}", node.DotnetProject);
                    parameters.Add($"Label{i}", node.Label.ToString());
                    parameters.Add($"Name{i}", Truncate(node.Name, 1000));
                    parameters.Add($"QualifiedName{i}", Truncate(node.QualifiedName, 1000));
                    parameters.Add($"FilePath{i}", node.FilePath ?? "");
                    parameters.Add($"StartLine{i}", node.StartLine);
                    parameters.Add($"EndLine{i}", node.EndLine);
                    parameters.Add($"Properties{i}", SerializeJson(node.Properties));
                    parameters.Add($"DoNotTrust{i}", node.DoNotTrust);
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            }

            foreach (var batch in nodesToInsert
                         .Select(node => Truncate(node.QualifiedName, 1000))
                         .Distinct(StringComparer.Ordinal)
                         .Chunk(1000))
            {
                var lookup = new DynamicParameters(new { Repository = repository });
                var rows = await conn.QueryAsync<(long Id, string QualifiedName)>(new CommandDefinition(
                    $"SELECT id AS Id, qualified_name AS QualifiedName FROM nodes WHERE repository = @Repository AND {QualifiedNameMatch(batch, lookup)}",
                    lookup, transaction,
                    commandTimeout: GraphWriteTimeout, cancellationToken: ct));
                foreach (var row in rows)
                    qnToId[row.QualifiedName] = row.Id;
            }

            var pendingByKey = new Dictionary<string, PendingEdge>(StringComparer.Ordinal);
            foreach (var incoming in preservedIncoming)
            {
                if (!Enum.TryParse<EdgeType>(incoming.Type, out var type))
                    continue;
                var pending = new PendingEdge(
                    incoming.SourceQN,
                    incoming.TargetQN,
                    type,
                    DeserializeJson(incoming.Properties));
                pendingByKey[EdgeKey(pending)] = pending;
            }
            foreach (var edge in edges)
                pendingByKey[EdgeKey(edge)] = edge;

            var resolvedEdges = InInsertOrder(ResolveFileSliceEdges(repository, pendingByKey.Values, qnToId));
            foreach (var batch in resolvedEdges.Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder("""
                    INSERT INTO edges (repository, source_id, target_id, type, properties)
                    VALUES
                    """);
                var parameters = new DynamicParameters();
                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.AppendLine($"(@Repository{i}, @SourceId{i}, @TargetId{i}, @Type{i}, @Properties{i})");
                    parameters.Add($"Repository{i}", repository);
                    parameters.Add($"SourceId{i}", batch[i].SourceId);
                    parameters.Add($"TargetId{i}", batch[i].TargetId);
                    parameters.Add($"Type{i}", batch[i].Type.ToString());
                    parameters.Add($"Properties{i}", SerializeJson(batch[i].Properties));
                }
                sql.AppendLine("ON DUPLICATE KEY UPDATE properties = VALUES(properties)");
                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            }

            if (retractions is { OrphanCandidateQNs.Count: > 0 })
                await DeleteOrphanedNodesAsync(conn, transaction, repository, retractions.OrphanCandidateQNs, ct);

            foreach (var batch in fileHashes.ToArray().Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder(
                    "INSERT INTO file_hashes (repository, rel_path, content_hash, extraction_version) VALUES ");
                var parameters = new DynamicParameters();
                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.Append($"(@Repository{i}, @RelPath{i}, @ContentHash{i}, @ExtractionVersion{i})");
                    parameters.Add($"Repository{i}", repository);
                    parameters.Add($"RelPath{i}", batch[i].Key);
                    parameters.Add($"ContentHash{i}", batch[i].Value);
                    parameters.Add($"ExtractionVersion{i}", extractionVersions?.GetValueOrDefault(batch[i].Key));
                }
                sql.Append(" ON DUPLICATE KEY UPDATE content_hash = VALUES(content_hash), " +
                           "extraction_version = VALUES(extraction_version)");
                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
            return resolvedEdges.Count;
        }
        catch
        {
            await TryRollbackAsync(transaction, repository);
            throw;
        }
    }

    public async Task<int> ReplaceRepositoryGraphAsync(
        string repository,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        RepositoryEntity repositoryEntity,
        SyncStateEntity? syncState,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
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
                repositoryEntity.Name,
                repositoryEntity.RepoUrl,
                repositoryEntity.SourceGroup,
                repositoryEntity.LocalPath,
                repositoryEntity.DefaultBranch,
                repositoryEntity.LastCommitSha,
                IndexedAt = repositoryEntity.IndexedAt ?? now,
                repositoryEntity.Language,
                repositoryEntity.Framework,
                repositoryEntity.IsFoundational,
                repositoryEntity.Properties,
                CreatedAt = repositoryEntity.CreatedAt == default ? now : repositoryEntity.CreatedAt,
                UpdatedAt = now
            }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));

            var replacedNodeIds = (await conn.QueryAsync<long>(new CommandDefinition(
                "SELECT id FROM nodes WHERE repository = @Repository",
                new { Repository = repository }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct))).ToList();
            await DeleteNodesInBatchesAsync(conn, transaction, replacedNodeIds, ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM file_hashes WHERE repository = @Repository",
                new { Repository = repository }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));

            var qnToId = new Dictionary<string, long>(nodes.Count, StringComparer.Ordinal);
            foreach (var batch in nodes.Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder("""
                    INSERT INTO nodes (repository, dotnet_project, label, name, qualified_name, file_path, start_line, end_line, properties, do_not_trust)
                    VALUES
                    """);
                var parameters = new DynamicParameters();

                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.AppendLine($"(@Repository{i}, @DotnetProject{i}, @Label{i}, @Name{i}, @QualifiedName{i}, @FilePath{i}, @StartLine{i}, @EndLine{i}, @Properties{i}, @DoNotTrust{i})");

                    var node = batch[i];
                    parameters.Add($"Repository{i}", node.Repository);
                    parameters.Add($"DotnetProject{i}", node.DotnetProject);
                    parameters.Add($"Label{i}", node.Label.ToString());
                    parameters.Add($"Name{i}", Truncate(node.Name, 1000));
                    parameters.Add($"QualifiedName{i}", Truncate(node.QualifiedName, 1000));
                    parameters.Add($"FilePath{i}", node.FilePath ?? "");
                    parameters.Add($"StartLine{i}", node.StartLine);
                    parameters.Add($"EndLine{i}", node.EndLine);
                    parameters.Add($"Properties{i}", SerializeJson(node.Properties));
                    parameters.Add($"DoNotTrust{i}", node.DoNotTrust);
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));

                var storedNames = batch
                    .Select(node => Truncate(node.QualifiedName, 1000))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var lookup = new DynamicParameters(new { Repository = repository });
                var rows = await conn.QueryAsync<(long id, string qualified_name)>(new CommandDefinition(
                    $"SELECT id, qualified_name FROM nodes WHERE repository = @Repository AND {QualifiedNameMatch(storedNames, lookup)}",
                    lookup, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
                var originalByStoredName = batch
                    .GroupBy(node => Truncate(node.QualifiedName, 1000), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().QualifiedName, StringComparer.Ordinal);

                foreach (var row in rows)
                {
                    if (originalByStoredName.TryGetValue(row.qualified_name, out var originalName))
                        qnToId[GraphNodeKey.Create(repository, originalName)] = row.id;
                }
            }

            var resolvedEdges = InInsertOrder(ResolveSnapshotEdges(repository, edges, qnToId));
            foreach (var batch in resolvedEdges.Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder("""
                    INSERT INTO edges (repository, source_id, target_id, type, properties)
                    VALUES
                    """);
                var parameters = new DynamicParameters();

                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.AppendLine($"(@Repository{i}, @SourceId{i}, @TargetId{i}, @Type{i}, @Properties{i})");
                    var edge = batch[i];
                    parameters.Add($"Repository{i}", edge.Repository);
                    parameters.Add($"SourceId{i}", edge.SourceId);
                    parameters.Add($"TargetId{i}", edge.TargetId);
                    parameters.Add($"Type{i}", edge.Type.ToString());
                    parameters.Add($"Properties{i}", SerializeJson(edge.Properties));
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            }

            foreach (var batch in fileHashes.ToArray().Chunk(options.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var sql = new StringBuilder("""
                    INSERT INTO file_hashes (repository, rel_path, content_hash, extraction_version)
                    VALUES
                    """);
                var parameters = new DynamicParameters();

                for (var i = 0; i < batch.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.AppendLine($"(@Repository{i}, @RelPath{i}, @ContentHash{i}, @ExtractionVersion{i})");
                    parameters.Add($"Repository{i}", repository);
                    parameters.Add($"RelPath{i}", batch[i].Key);
                    parameters.Add($"ContentHash{i}", batch[i].Value);
                    parameters.Add($"ExtractionVersion{i}", extractionVersions?.GetValueOrDefault(batch[i].Key));
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    sql.ToString(), parameters, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            }

            if (syncState is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO sync_state (repository, last_sync_at, last_commit_sha, status, error_message,
                                            failed_commit_sha, consecutive_failures, last_failure_at,
                                            extraction_fingerprint)
                    VALUES (@Repository, @LastSyncAt, @LastCommitSha, @Status, @ErrorMessage,
                            @FailedCommitSha, @ConsecutiveFailures, @LastFailureAt,
                            @ExtractionFingerprint)
                    ON DUPLICATE KEY UPDATE
                        last_sync_at = VALUES(last_sync_at),
                        last_commit_sha = COALESCE(VALUES(last_commit_sha), last_commit_sha),
                        extraction_fingerprint = COALESCE(VALUES(extraction_fingerprint), extraction_fingerprint),
                        status = VALUES(status),
                        error_message = VALUES(error_message),
                        failed_commit_sha = VALUES(failed_commit_sha),
                        consecutive_failures = VALUES(consecutive_failures),
                        last_failure_at = VALUES(last_failure_at)
                    """, syncState, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
            return resolvedEdges.Count;
        }
        catch
        {
            await TryRollbackAsync(transaction, repository);
            throw;
        }
    }

    public async Task DeleteFilesFromRepositoryGraphAsync(
        string repository,
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
                    WHERE n.repository = @Repository AND n.file_path IN @Paths
                    """, new { Repository = repository, Paths = paths }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM nodes WHERE repository = @Repository AND file_path IN @Paths",
                    new { Repository = repository, Paths = paths }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM file_hashes WHERE repository = @Repository AND rel_path IN @Paths",
                    new { Repository = repository, Paths = paths }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await TryRollbackAsync(transaction, repository);
            throw;
        }
    }

    private int GraphWriteTimeout => Math.Max(0, options.GraphWriteCommandTimeoutSeconds);

    /// <summary>
    /// Deletes nodes with their analysis rows and edges a bounded batch at a time. One statement
    /// over a large repository's slice cascaded to hundreds of thousands of edges and ran for
    /// minutes; batches keep each statement short and let cancellation take effect between them.
    /// Deleting the edges explicitly before the nodes (rather than through ON DELETE CASCADE) is
    /// ~40% faster for a 130k-node / 900k-edge slice; anything still referencing the nodes (e.g.
    /// cross-repository rows) continues to cascade.
    /// </summary>
    private async Task DeleteNodesInBatchesAsync(
        MySqlConnection conn,
        MySqlTransaction transaction,
        IReadOnlyList<long> nodeIds,
        CancellationToken ct)
    {
        foreach (var batch in nodeIds.Order().Chunk(Math.Max(1, options.GraphDeleteBatchSize)))
        {
            ct.ThrowIfCancellationRequested();
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM node_analysis WHERE node_id IN @Ids",
                new { Ids = batch }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM edges WHERE source_id IN @Ids",
                new { Ids = batch }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM edges WHERE target_id IN @Ids",
                new { Ids = batch }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM nodes WHERE id IN @Ids",
                new { Ids = batch }, transaction, commandTimeout: GraphWriteTimeout, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Rolls back after a failed snapshot write without masking the failure: a command timeout or
    /// network error leaves the connection Broken, and a throwing rollback would otherwise replace
    /// the real cause with "Connection must be Open; current state is Broken".
    /// </summary>
    private async Task TryRollbackAsync(MySqlTransaction transaction, string repository)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            logger.LogWarning(rollbackException,
                "Rollback of the graph write for {Repository} failed; the server discards the uncommitted transaction with the connection",
                repository);
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
        string repository,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, long> qnToId)
    {
        var resolved = new List<GraphEdge>(edges.Count);
        var edgeKeys = new HashSet<(long SourceId, long TargetId, EdgeType Type)>();
        foreach (var edge in edges)
        {
            if (!qnToId.TryGetValue(GraphNodeKey.Create(repository, edge.SourceQN), out var sourceId) ||
                !qnToId.TryGetValue(GraphNodeKey.Create(repository, edge.TargetQN), out var targetId))
            {
                continue;
            }

            if (!edgeKeys.Add((sourceId, targetId, edge.Type)))
                continue;

            resolved.Add(new GraphEdge
            {
                Repository = repository,
                SourceId = sourceId,
                TargetId = targetId,
                Type = edge.Type,
                Properties = edge.Properties ?? []
            });
        }

        return resolved;
    }

    private static List<GraphEdge> ResolveFileSliceEdges(
        string repository,
        IEnumerable<PendingEdge> edges,
        IReadOnlyDictionary<string, long> qnToId)
    {
        var resolved = new List<GraphEdge>();
        foreach (var edge in edges)
        {
            if (!qnToId.TryGetValue(Truncate(edge.SourceQN, 1000), out var sourceId) ||
                !qnToId.TryGetValue(Truncate(edge.TargetQN, 1000), out var targetId))
                continue;
            resolved.Add(new GraphEdge
            {
                Repository = repository,
                SourceId = sourceId,
                TargetId = targetId,
                Type = edge.Type,
                Properties = edge.Properties ?? []
            });
        }
        return resolved;
    }

    /// <summary>
    /// Orders edges by (source, target) for insertion. Pending edges arrive in extraction order,
    /// which scatters every insert across the edge indexes; with uq_edge/idx_edges_source and the
    /// auto-increment key all following source order, inserting the inference graph's ~730k edges
    /// was ~9x faster in isolation (50 s random vs 5.8 s sorted, 128 MB buffer pool), and the next
    /// replacement's per-source deletes then touch contiguous pages. The sort is stable, so edges
    /// that collide on one key keep their relative order (and ON DUPLICATE KEY outcome).
    /// </summary>
    private static List<GraphEdge> InInsertOrder(List<GraphEdge> edges) =>
        edges.OrderBy(edge => edge.SourceId).ThenBy(edge => edge.TargetId).ToList();

    private static string EdgeKey(PendingEdge edge) =>
        $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}";

    private sealed record StoredPendingEdge(
        string SourceQN,
        string TargetQN,
        string Type,
        string? Properties);
}
