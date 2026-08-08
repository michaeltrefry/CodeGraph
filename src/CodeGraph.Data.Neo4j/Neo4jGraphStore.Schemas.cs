using System.Text.Json;
using CodeGraph.Data;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    private const string SchemaMetadataProjection = """
        MATCH (r:RepositoryRecord)
        WITH r,
            CASE
                WHEN r.schemaServerName IS NOT NULL THEN r.schemaServerName
                WHEN r.properties CONTAINS '"serverName"' THEN split(split(r.properties, '"serverName"')[1], '"')[1]
                ELSE coalesce(r.sourceGroup, '')
            END AS serverName,
            CASE
                WHEN r.schemaDatabaseName IS NOT NULL THEN r.schemaDatabaseName
                WHEN r.properties CONTAINS '"databaseName"' THEN split(split(r.properties, '"databaseName"')[1], '"')[1]
                ELSE last(split(
                    CASE WHEN toLower(r.name) STARTS WITH 'db:' THEN substring(r.name, 3) ELSE r.name END,
                    '/'))
            END AS databaseName,
            coalesce(
                r.isDatabaseSchema,
                toLower(r.name) STARTS WITH 'db:'
                    OR r.properties CONTAINS '"serverName"'
                    OR r.properties CONTAINS '"databaseName"') AS isDatabaseSchema
        """;

    private const string FilteredSchemaPredicate = """
        isDatabaseSchema
        AND serverName =~ '(?s).*\\S.*'
        AND databaseName =~ '(?s).*\\S.*'
        AND ($server IS NULL OR toLower(serverName) = toLower($server))
        AND ($database IS NULL OR toLower(databaseName) = toLower($database))
        AND ($search IS NULL
            OR toLower(r.name) CONTAINS toLower($search)
            OR toLower(serverName) CONTAINS toLower($search)
            OR toLower(databaseName) CONTAINS toLower($search))
        """;

    private const string DeterministicSchemaOrder = """
        toLower(serverName), serverName,
        toLower(databaseName), databaseName,
        toLower(r.name), r.name
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
            search = NormalizeSchemaFilter(search),
            server = NormalizeSchemaFilter(server),
            database = NormalizeSchemaFilter(database),
            skip = ((long)page - 1) * pageSize,
            limit = pageSize
        };

        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var pageCursor = await tx.RunAsync($"""
                {SchemaMetadataProjection}
                WHERE {FilteredSchemaPredicate}
                WITH r, serverName, databaseName
                ORDER BY {DeterministicSchemaOrder}
                SKIP $skip LIMIT $limit
                OPTIONAL MATCH (n:CodeNode)
                WHERE n.project = r.name AND n.label IN ['Table', 'View', 'StoredProcedure']
                RETURN r, serverName, databaseName,
                    sum(CASE WHEN n.label = 'Table' THEN 1 ELSE 0 END) AS tableCount,
                    sum(CASE WHEN n.label = 'View' THEN 1 ELSE 0 END) AS viewCount,
                    sum(CASE WHEN n.label = 'StoredProcedure' THEN 1 ELSE 0 END) AS procedureCount
                ORDER BY {DeterministicSchemaOrder}
                """, parameters);

            var items = new List<SchemaRepositoryItem>();
            await foreach (var record in pageCursor)
            {
                items.Add(new SchemaRepositoryItem(
                    MapRepositoryNode(record["r"].As<INode>()),
                    record["serverName"].As<string>(),
                    record["databaseName"].As<string>(),
                    record["tableCount"].As<int>(),
                    record["viewCount"].As<int>(),
                    record["procedureCount"].As<int>()));
            }

            var totalsCursor = await tx.RunAsync($"""
                {SchemaMetadataProjection}
                WHERE {FilteredSchemaPredicate}
                OPTIONAL MATCH (n:CodeNode)
                WHERE n.project = r.name AND n.label IN ['Table', 'View', 'StoredProcedure']
                RETURN count(DISTINCT r) AS totalCount,
                    sum(CASE WHEN n.label = 'Table' THEN 1 ELSE 0 END) AS totalTables,
                    sum(CASE WHEN n.label = 'View' THEN 1 ELSE 0 END) AS totalViews,
                    sum(CASE WHEN n.label = 'StoredProcedure' THEN 1 ELSE 0 END) AS totalProcedures
                """, parameters);
            await totalsCursor.FetchAsync();
            var totals = totalsCursor.Current;

            var serverCursor = await tx.RunAsync($"""
                {SchemaMetadataProjection}
                WHERE isDatabaseSchema
                    AND serverName =~ '(?s).*\\S.*'
                    AND databaseName =~ '(?s).*\\S.*'
                WITH toLower(serverName) AS optionKey, min(serverName) AS optionValue
                RETURN optionValue
                ORDER BY optionKey, optionValue
                """);
            var servers = new List<string>();
            await foreach (var record in serverCursor)
                servers.Add(record["optionValue"].As<string>());

            var databaseCursor = await tx.RunAsync($"""
                {SchemaMetadataProjection}
                WHERE isDatabaseSchema
                    AND serverName =~ '(?s).*\\S.*'
                    AND databaseName =~ '(?s).*\\S.*'
                    AND ($server IS NULL OR toLower(serverName) = toLower($server))
                WITH toLower(databaseName) AS optionKey, min(databaseName) AS optionValue
                RETURN optionValue
                ORDER BY optionKey, optionValue
                """, parameters);
            var databases = new List<string>();
            await foreach (var record in databaseCursor)
                databases.Add(record["optionValue"].As<string>());

            return new SchemaRepositorySearchResult(
                items,
                totals["totalCount"].As<int>(),
                totals["totalTables"].As<int>(),
                totals["totalViews"].As<int>(),
                totals["totalProcedures"].As<int>(),
                servers,
                databases);
        });
    }

    private static SchemaMetadata ResolveSchemaMetadata(RepositoryEntity repository)
    {
        var serverName = GetJsonString(repository.Properties, "serverName") ?? repository.SourceGroup ?? "";
        var databaseName = GetJsonString(repository.Properties, "databaseName") ?? GetDatabaseNameFromProject(repository.Name);
        var isDatabaseSchema = repository.Name.StartsWith("db:", StringComparison.OrdinalIgnoreCase) ||
            GetJsonString(repository.Properties, "serverName") is not null ||
            GetJsonString(repository.Properties, "databaseName") is not null;

        return isDatabaseSchema && !string.IsNullOrWhiteSpace(serverName) && !string.IsNullOrWhiteSpace(databaseName)
            ? new SchemaMetadata(true, serverName, databaseName)
            : new SchemaMetadata(isDatabaseSchema, null, null);
    }

    private static string? GetJsonString(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetDatabaseNameFromProject(string projectName)
    {
        var name = projectName.StartsWith("db:", StringComparison.OrdinalIgnoreCase)
            ? projectName[3..]
            : projectName;
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static string? NormalizeSchemaFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record SchemaMetadata(bool IsDatabaseSchema, string? ServerName, string? DatabaseName);
}
