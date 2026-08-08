using Dapper;

namespace CodeGraph.Data.MariaDb;

public partial class MySqlGraphStore
{
    private const string SchemaRepositoryCte = """
        WITH schema_repositories AS (
            SELECT
                r.name,
                r.repo_url,
                r.gitlab_group,
                r.local_path,
                r.last_commit_sha,
                r.indexed_at,
                r.language,
                r.framework,
                r.is_foundational,
                r.properties,
                COALESCE(
                    NULLIF(JSON_UNQUOTE(JSON_EXTRACT(r.properties, '$.serverName')), 'null'),
                    r.gitlab_group,
                    '') AS server_name,
                COALESCE(
                    NULLIF(JSON_UNQUOTE(JSON_EXTRACT(r.properties, '$.databaseName')), 'null'),
                    SUBSTRING_INDEX(
                        CASE WHEN LOWER(r.name) LIKE 'db:%' THEN SUBSTRING(r.name, 4) ELSE r.name END,
                        '/',
                        -1)) AS database_name
            FROM repositories r
            WHERE LOWER(r.name) LIKE 'db:%'
                OR JSON_EXTRACT(r.properties, '$.serverName') IS NOT NULL
                OR JSON_EXTRACT(r.properties, '$.databaseName') IS NOT NULL
        )
        """;

    private const string ValidSchemaPredicate =
        "server_name REGEXP '[^[:space:]]' AND database_name REGEXP '[^[:space:]]'";
    private const string FilteredSchemaPredicate = """
        server_name REGEXP '[^[:space:]]'
        AND database_name REGEXP '[^[:space:]]'
        AND (@Server IS NULL OR LOWER(server_name) = LOWER(@Server))
        AND (@Database IS NULL OR LOWER(database_name) = LOWER(@Database))
        AND (@Search IS NULL
            OR LOCATE(LOWER(@Search), LOWER(name)) > 0
            OR LOCATE(LOWER(@Search), LOWER(server_name)) > 0
            OR LOCATE(LOWER(@Search), LOWER(database_name)) > 0)
        """;

    private const string DeterministicSchemaOrder = """
        LOWER(server_name), BINARY server_name,
        LOWER(database_name), BINARY database_name,
        LOWER(name), BINARY name
        """;

    public async Task<SchemaRepositorySearchResult> SearchSchemaRepositoriesAsync(
        string? search = null,
        string? server = null,
        string? database = null,
        int page = 1,
        int pageSize = 25)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var parameters = new
        {
            Search = NormalizeSchemaFilter(search),
            Server = NormalizeSchemaFilter(server),
            Database = NormalizeSchemaFilter(database),
            Offset = ((long)page - 1) * pageSize,
            PageSize = pageSize
        };

        await using var connection = await GetOpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

        var pageSql = $"""
            {SchemaRepositoryCte},
            filtered_schemas AS (
                SELECT *
                FROM schema_repositories
                WHERE {FilteredSchemaPredicate}
            ),
            paged_schemas AS (
                SELECT *
                FROM filtered_schemas
                ORDER BY {DeterministicSchemaOrder}
                LIMIT @PageSize OFFSET @Offset
            )
            SELECT
                p.name AS Name,
                p.repo_url AS RepoUrl,
                p.gitlab_group AS SourceGroup,
                p.local_path AS LocalPath,
                p.last_commit_sha AS LastCommitSha,
                p.indexed_at AS IndexedAt,
                p.language AS Language,
                p.framework AS Framework,
                p.is_foundational AS IsFoundational,
                p.properties AS PropertiesJson,
                p.server_name AS ServerName,
                p.database_name AS DatabaseName,
                COALESCE(SUM(CASE WHEN n.label = 'Table' THEN 1 ELSE 0 END), 0) AS TableCount,
                COALESCE(SUM(CASE WHEN n.label = 'View' THEN 1 ELSE 0 END), 0) AS ViewCount,
                COALESCE(SUM(CASE WHEN n.label = 'StoredProcedure' THEN 1 ELSE 0 END), 0) AS ProcedureCount
            FROM paged_schemas p
            LEFT JOIN nodes n
                ON n.project = p.name
                AND n.label IN ('Table', 'View', 'StoredProcedure')
            GROUP BY
                p.name, p.repo_url, p.gitlab_group, p.local_path, p.last_commit_sha,
                p.indexed_at, p.language, p.framework, p.is_foundational, p.properties,
                p.server_name, p.database_name
            ORDER BY
                LOWER(p.server_name), BINARY p.server_name,
                LOWER(p.database_name), BINARY p.database_name,
                LOWER(p.name), BINARY p.name
            """;
        var pageRows = (await connection.QueryAsync<SchemaRepositoryRow>(pageSql, parameters, transaction)).ToList();

        var totalsSql = $"""
            {SchemaRepositoryCte},
            filtered_schemas AS (
                SELECT *
                FROM schema_repositories
                WHERE {FilteredSchemaPredicate}
            )
            SELECT
                COUNT(DISTINCT f.name) AS TotalCount,
                COALESCE(SUM(CASE WHEN n.label = 'Table' THEN 1 ELSE 0 END), 0) AS TotalTables,
                COALESCE(SUM(CASE WHEN n.label = 'View' THEN 1 ELSE 0 END), 0) AS TotalViews,
                COALESCE(SUM(CASE WHEN n.label = 'StoredProcedure' THEN 1 ELSE 0 END), 0) AS TotalProcedures
            FROM filtered_schemas f
            LEFT JOIN nodes n
                ON n.project = f.name
                AND n.label IN ('Table', 'View', 'StoredProcedure')
            """;
        var totals = await connection.QuerySingleAsync<SchemaRepositoryTotals>(totalsSql, parameters, transaction);

        var serversSql = $"""
            {SchemaRepositoryCte}
            SELECT CONVERT(MIN(BINARY server_name) USING utf8mb4) AS Value
            FROM schema_repositories
            WHERE {ValidSchemaPredicate}
            GROUP BY LOWER(server_name)
            ORDER BY LOWER(server_name), BINARY MIN(server_name)
            """;
        var servers = (await connection.QueryAsync<string>(serversSql, transaction: transaction)).ToList();

        var databasesSql = $"""
            {SchemaRepositoryCte}
            SELECT CONVERT(MIN(BINARY database_name) USING utf8mb4) AS Value
            FROM schema_repositories
            WHERE {ValidSchemaPredicate}
                AND (@Server IS NULL OR LOWER(server_name) = LOWER(@Server))
            GROUP BY LOWER(database_name)
            ORDER BY LOWER(database_name), BINARY MIN(database_name)
            """;
        var databases = (await connection.QueryAsync<string>(databasesSql, parameters, transaction)).ToList();

        var items = pageRows.Select(row => new SchemaRepositoryItem(
            new ProjectInfo(
                row.Name,
                row.RepoUrl,
                row.SourceGroup,
                row.LocalPath,
                row.LastCommitSha,
                row.IndexedAt,
                row.Language,
                row.Framework,
                row.IsFoundational,
                DeserializeJson(row.PropertiesJson)),
            row.ServerName,
            row.DatabaseName,
            row.TableCount,
            row.ViewCount,
            row.ProcedureCount)).ToList();

        await transaction.CommitAsync();

        return new SchemaRepositorySearchResult(
            items,
            totals.TotalCount,
            totals.TotalTables,
            totals.TotalViews,
            totals.TotalProcedures,
            servers,
            databases);
    }

    private static string? NormalizeSchemaFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class SchemaRepositoryRow
    {
        public string Name { get; init; } = "";
        public string? RepoUrl { get; init; }
        public string? SourceGroup { get; init; }
        public string? LocalPath { get; init; }
        public string? LastCommitSha { get; init; }
        public DateTime? IndexedAt { get; init; }
        public string? Language { get; init; }
        public string? Framework { get; init; }
        public bool IsFoundational { get; init; }
        public string? PropertiesJson { get; init; }
        public string ServerName { get; init; } = "";
        public string DatabaseName { get; init; } = "";
        public int TableCount { get; init; }
        public int ViewCount { get; init; }
        public int ProcedureCount { get; init; }
    }

    private sealed class SchemaRepositoryTotals
    {
        public int TotalCount { get; init; }
        public int TotalTables { get; init; }
        public int TotalViews { get; init; }
        public int TotalProcedures { get; init; }
    }
}
