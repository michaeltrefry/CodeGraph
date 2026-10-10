using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// MariaDB persistence for branch overlays. Overlay rows live in dedicated branch_overlay_*
/// tables and never touch the default-branch nodes/edges tables.
/// </summary>
public class MySqlBranchOverlayStore(CodeGraphDbContext db) : IBranchOverlayStore
{
    private const int InsertBatchSize = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string OverlayColumns = """
        id AS Id,
        project AS Project,
        requested_ref AS RequestedRef,
        head_sha AS HeadSha,
        base_sha AS BaseSha,
        status AS Status,
        error AS Error,
        requested_by_username AS RequestedByUsername,
        indexer_run_id AS IndexerRunId,
        changed_file_count AS ChangedFileCount,
        node_count AS NodeCount,
        edge_count AS EdgeCount,
        created_at AS CreatedAt,
        started_at AS StartedAt,
        completed_at AS CompletedAt,
        expired_at AS ExpiredAt
        """;

    public async Task<long> CreateOverlayAsync(BranchOverlayEntity overlay, CancellationToken ct = default)
    {
        if (overlay.CreatedAt == default)
            overlay.CreatedAt = DateTime.UtcNow;

        var conn = await GetOpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO branch_overlays
                (project, requested_ref, head_sha, base_sha, status, error, requested_by_username,
                 indexer_run_id, created_at)
            VALUES
                (@Project, @RequestedRef, @HeadSha, @BaseSha, @Status, @Error, @RequestedByUsername,
                 @IndexerRunId, @CreatedAt);
            SELECT LAST_INSERT_ID();
            """, overlay, cancellationToken: ct));
        overlay.Id = id;
        return id;
    }

    public async Task<BranchOverlayEntity?> GetOverlayAsync(long overlayId, CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BranchOverlayEntity>(new CommandDefinition(
            $"SELECT {OverlayColumns} FROM branch_overlays WHERE id = @Id",
            new { Id = overlayId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<BranchOverlayEntity>> FindOverlaysByShaAsync(
        string sha,
        string? project,
        CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        return (await conn.QueryAsync<BranchOverlayEntity>(new CommandDefinition($"""
            SELECT {OverlayColumns}
            FROM branch_overlays
            WHERE (head_sha = @Sha OR (head_sha IS NULL AND requested_ref = @Sha))
              AND (@Project IS NULL OR project = @Project)
            ORDER BY created_at DESC, id DESC
            """, new { Sha = sha, Project = project }, cancellationToken: ct))).ToList();
    }

    public async Task SetOverlayRunAsync(long overlayId, long indexerRunId, CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE branch_overlays SET indexer_run_id = @RunId WHERE id = @Id",
            new { Id = overlayId, RunId = indexerRunId }, cancellationToken: ct));
    }

    public async Task<bool> MarkOverlayIndexingAsync(long overlayId, DateTime startedAt, CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE branch_overlays
            SET status = @Status, started_at = @StartedAt, completed_at = NULL, error = NULL
            WHERE id = @Id AND status <> @Expired
            """,
            new
            {
                Id = overlayId,
                Status = BranchOverlayStatuses.Indexing,
                Expired = BranchOverlayStatuses.Expired,
                StartedAt = startedAt
            }, cancellationToken: ct));
        return updated > 0;
    }

    public async Task<bool> CompleteOverlayAsync(
        long overlayId,
        string headSha,
        string baseSha,
        IReadOnlyList<BranchOverlayFileEntity> files,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        DateTime completedAt,
        CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);

        // Lock the overlay row so a concurrent TTL sweep cannot interleave with the rewrite.
        var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM branch_overlays WHERE id = @Id FOR UPDATE",
            new { Id = overlayId }, transaction, cancellationToken: ct));
        if (status is null or BranchOverlayStatuses.Expired)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await DeleteChildRowsAsync(conn, transaction, [overlayId], ct);

        foreach (var batch in files.Chunk(InsertBatchSize))
        {
            var sql = new StringBuilder(
                "INSERT INTO branch_overlay_files (overlay_id, file_path, change_kind, previous_path) VALUES ");
            var parameters = new DynamicParameters();
            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0) sql.Append(',');
                sql.Append($"(@OverlayId, @FilePath{i}, @ChangeKind{i}, @PreviousPath{i})");
                parameters.Add($"FilePath{i}", batch[i].FilePath);
                parameters.Add($"ChangeKind{i}", batch[i].ChangeKind);
                parameters.Add($"PreviousPath{i}", batch[i].PreviousPath);
            }

            parameters.Add("OverlayId", overlayId);
            await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct));
        }

        foreach (var batch in nodes.Chunk(InsertBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO branch_overlay_nodes
                    (overlay_id, dotnet_project, label, name, qualified_name, file_path, start_line, end_line, properties)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0) sql.Append(',');
                sql.Append($"(@OverlayId, @DotnetProject{i}, @Label{i}, @Name{i}, @QualifiedName{i}, @FilePath{i}, @StartLine{i}, @EndLine{i}, @Properties{i})");
                var node = batch[i];
                parameters.Add($"DotnetProject{i}", node.DotnetProject);
                parameters.Add($"Label{i}", node.Label.ToString());
                parameters.Add($"Name{i}", Truncate(node.Name, 255));
                parameters.Add($"QualifiedName{i}", node.QualifiedName);
                parameters.Add($"FilePath{i}", node.FilePath ?? "");
                parameters.Add($"StartLine{i}", node.StartLine);
                parameters.Add($"EndLine{i}", node.EndLine);
                parameters.Add($"Properties{i}", SerializeJson(node.Properties));
            }

            parameters.Add("OverlayId", overlayId);
            sql.Append(" ON DUPLICATE KEY UPDATE id = id");
            await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct));
        }

        foreach (var batch in edges.Chunk(InsertBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO branch_overlay_edges
                    (overlay_id, source_qualified_name, target_qualified_name, type, properties)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var i = 0; i < batch.Length; i++)
            {
                if (i > 0) sql.Append(',');
                sql.Append($"(@OverlayId, @Source{i}, @Target{i}, @Type{i}, @Properties{i})");
                parameters.Add($"Source{i}", batch[i].SourceQN);
                parameters.Add($"Target{i}", batch[i].TargetQN);
                parameters.Add($"Type{i}", batch[i].Type.ToString());
                parameters.Add($"Properties{i}", SerializeJson(batch[i].Properties));
            }

            parameters.Add("OverlayId", overlayId);
            await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE branch_overlays
            SET status = @Status,
                head_sha = @HeadSha,
                base_sha = @BaseSha,
                error = NULL,
                changed_file_count = @FileCount,
                node_count = @NodeCount,
                edge_count = @EdgeCount,
                completed_at = @CompletedAt
            WHERE id = @Id
            """,
            new
            {
                Id = overlayId,
                Status = BranchOverlayStatuses.Ready,
                HeadSha = headSha,
                BaseSha = baseSha,
                FileCount = files.Count,
                NodeCount = nodes.Count,
                EdgeCount = edges.Count,
                CompletedAt = completedAt
            }, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task MarkOverlayFailedAsync(
        long overlayId,
        string error,
        string? headSha,
        string? baseSha,
        DateTime completedAt,
        CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE branch_overlays
            SET status = @Status,
                error = @Error,
                head_sha = COALESCE(@HeadSha, head_sha),
                base_sha = COALESCE(@BaseSha, base_sha),
                completed_at = @CompletedAt
            WHERE id = @Id AND status <> @Expired
            """,
            new
            {
                Id = overlayId,
                Status = BranchOverlayStatuses.Failed,
                Expired = BranchOverlayStatuses.Expired,
                Error = Truncate(error, 8000),
                HeadSha = headSha,
                BaseSha = baseSha,
                CompletedAt = completedAt
            }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<BranchOverlayFileEntity>> GetOverlayFilesAsync(long overlayId, CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        return (await conn.QueryAsync<BranchOverlayFileEntity>(new CommandDefinition("""
            SELECT overlay_id AS OverlayId, file_path AS FilePath, change_kind AS ChangeKind, previous_path AS PreviousPath
            FROM branch_overlay_files
            WHERE overlay_id = @Id
            ORDER BY file_path
            """, new { Id = overlayId }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<GraphNode>> GetOverlayNodesAsync(long overlayId, CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<OverlayNodeRow>(new CommandDefinition("""
            SELECT n.id AS Id, o.project AS Project, n.dotnet_project AS DotnetProject, n.label AS Label,
                   n.name AS Name, n.qualified_name AS QualifiedName, n.file_path AS FilePath,
                   n.start_line AS StartLine, n.end_line AS EndLine, n.properties AS Properties
            FROM branch_overlay_nodes n
            INNER JOIN branch_overlays o ON o.id = n.overlay_id
            WHERE n.overlay_id = @Id
            ORDER BY n.id
            """, new { Id = overlayId }, cancellationToken: ct));

        return rows.Select(row => new GraphNode
        {
            Id = row.Id,
            Project = row.Project,
            DotnetProject = row.DotnetProject,
            Label = Enum.Parse<NodeLabel>(row.Label),
            Name = row.Name,
            QualifiedName = row.QualifiedName,
            FilePath = row.FilePath ?? "",
            StartLine = row.StartLine,
            EndLine = row.EndLine,
            Properties = DeserializeJson(row.Properties) ?? new()
        }).ToList();
    }

    public async Task<IReadOnlyList<PendingEdge>> GetOverlayEdgesAsync(long overlayId, CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<OverlayEdgeRow>(new CommandDefinition("""
            SELECT source_qualified_name AS SourceQN, target_qualified_name AS TargetQN,
                   type AS Type, properties AS Properties
            FROM branch_overlay_edges
            WHERE overlay_id = @Id
            ORDER BY id
            """, new { Id = overlayId }, cancellationToken: ct));

        return rows.Select(row => new PendingEdge(
            row.SourceQN,
            row.TargetQN,
            Enum.Parse<EdgeType>(row.Type),
            DeserializeJson(row.Properties))).ToList();
    }

    public async Task<IReadOnlyList<long>> ExpireOverlaysAsync(
        DateTime createdAtOrBefore,
        DateTime expiredAt,
        CancellationToken ct = default)
    {
        var conn = await GetOpenConnectionAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        var ids = (await conn.QueryAsync<long>(new CommandDefinition("""
            SELECT id FROM branch_overlays
            WHERE status <> @Expired AND created_at <= @Cutoff
            ORDER BY id
            FOR UPDATE
            """,
            new { Expired = BranchOverlayStatuses.Expired, Cutoff = createdAtOrBefore },
            transaction, cancellationToken: ct))).ToList();

        if (ids.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return [];
        }

        await DeleteChildRowsAsync(conn, transaction, ids, ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE branch_overlays
            SET status = @Expired, expired_at = @ExpiredAt
            WHERE id IN @Ids
            """,
            new { Expired = BranchOverlayStatuses.Expired, ExpiredAt = expiredAt, Ids = ids },
            transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return ids;
    }

    private static async Task DeleteChildRowsAsync(
        DbConnection conn,
        DbTransaction transaction,
        IReadOnlyList<long> overlayIds,
        CancellationToken ct)
    {
        foreach (var table in new[] { "branch_overlay_edges", "branch_overlay_nodes", "branch_overlay_files" })
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE overlay_id IN @Ids",
                new { Ids = overlayIds }, transaction, cancellationToken: ct));
        }
    }

    private async Task<DbConnection> GetOpenConnectionAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
        return connection;
    }

    private static string? SerializeJson(IReadOnlyDictionary<string, object>? values)
        => values is { Count: > 0 } ? JsonSerializer.Serialize(values, JsonOptions) : null;

    private static Dictionary<string, object>? DeserializeJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private sealed class OverlayNodeRow
    {
        public long Id { get; init; }
        public string Project { get; init; } = "";
        public string? DotnetProject { get; init; }
        public string Label { get; init; } = "";
        public string Name { get; init; } = "";
        public string QualifiedName { get; init; } = "";
        public string? FilePath { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public string? Properties { get; init; }
    }

    private sealed class OverlayEdgeRow
    {
        public string SourceQN { get; init; } = "";
        public string TargetQN { get; init; } = "";
        public string Type { get; init; } = "";
        public string? Properties { get; init; }
    }
}
