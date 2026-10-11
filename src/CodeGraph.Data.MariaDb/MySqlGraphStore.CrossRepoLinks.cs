using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CodeGraph.Models;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

/// <summary>
/// Cross-repo linking reads: server-side filtered, column-projected and streamed row by row on a
/// dedicated connection, so linking a small repository never materializes the node/edge rows (and
/// their JSON properties) of every other project in the indexer's heap.
/// </summary>
public partial class MySqlGraphStore
{
    private static readonly Regex PropertyKeyPattern = new("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);

    public async IAsyncEnumerable<LinkNodeRow> StreamLinkNodesAsync(
        LinkNodeQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (query.Labels.Count == 0)
            yield break;

        await using var conn = await GetOpenConnectionAsync();
        await using var command = conn.CreateCommand();
        command.CommandTimeout = GraphWriteTimeout;

        var sql = new StringBuilder("SELECT n.id, n.project, n.label, n.name, n.qualified_name");
        foreach (var key in query.PropertyKeys)
            sql.Append(", ").Append(JsonScalar("n.properties", key));
        sql.Append(" FROM nodes n WHERE n.label IN (")
            .Append(AddListParameters(command, "label", query.Labels.Select(label => label.ToString())))
            .Append(')');
        AppendPropertyFilters(sql, command, "n.properties", "nf", query.PropertyEquals);
        command.CommandText = sql.ToString();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new LinkNodeRow(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<NodeLabel>(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                ReadProperties(reader, 5, query.PropertyKeys));
        }
    }

    public async IAsyncEnumerable<LinkEdgeRow> StreamLinkEdgesAsync(
        LinkEdgeQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (query.Types.Count == 0 || query.TargetLabels is { Count: 0 })
            yield break;

        await using var conn = await GetOpenConnectionAsync();
        await using var command = conn.CreateCommand();
        command.CommandTimeout = GraphWriteTimeout;

        // Without a target filter an edge is kept even if its target row is missing (LEFT JOIN);
        // the FK makes that impossible today, but the projection must not silently drop edges.
        var targetJoin = CrossRepoLinkProjection.HasTargetFilter(query) ? "JOIN" : "LEFT JOIN";
        var sql = new StringBuilder(
            "SELECT e.source_id, s.project, e.target_id, COALESCE(t.project, ''), t.label, " +
            "COALESCE(t.name, ''), COALESCE(t.qualified_name, ''), e.type");
        foreach (var key in query.EdgePropertyKeys)
            sql.Append(", ").Append(JsonScalar("e.properties", key));
        foreach (var key in query.TargetPropertyKeys)
            sql.Append(", ").Append(JsonScalar("t.properties", key));
        sql.Append(" FROM edges e ")
            .Append(targetJoin)
            .Append(" nodes t ON t.id = e.target_id JOIN nodes s ON s.id = e.source_id WHERE e.type IN (")
            .Append(AddListParameters(command, "type", query.Types.Select(type => type.ToString())))
            .Append(')');
        if (query.TargetLabels is not null)
        {
            sql.Append(" AND t.label IN (")
                .Append(AddListParameters(command, "label", query.TargetLabels.Select(label => label.ToString())))
                .Append(')');
        }
        AppendPropertyFilters(sql, command, "t.properties", "tf", query.TargetPropertyEquals);
        command.CommandText = sql.ToString();

        var edgePropertyOrdinal = 8;
        var targetPropertyOrdinal = edgePropertyOrdinal + query.EdgePropertyKeys.Count;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new LinkEdgeRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Enum.Parse<NodeLabel>(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                Enum.Parse<EdgeType>(reader.GetString(7)),
                ReadProperties(reader, edgePropertyOrdinal, query.EdgePropertyKeys),
                ReadProperties(reader, targetPropertyOrdinal, query.TargetPropertyKeys));
        }
    }

    private static string JsonScalar(string column, string key)
    {
        if (!PropertyKeyPattern.IsMatch(key))
            throw new ArgumentException($"Invalid property key '{key}'.", nameof(key));

        // A JSON null reads back as the text 'null'; treat it like a missing key.
        return $"NULLIF(JSON_UNQUOTE(JSON_EXTRACT({column}, '$.\"{key}\"')), 'null')";
    }

    private static string AddListParameters(MySqlCommand command, string prefix, IEnumerable<string> values)
    {
        var names = new List<string>();
        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            var name = $"@{prefix}{names.Count}";
            command.Parameters.AddWithValue(name, value);
            names.Add(name);
        }

        return string.Join(", ", names);
    }

    private static void AppendPropertyFilters(
        StringBuilder sql,
        MySqlCommand command,
        string column,
        string prefix,
        IReadOnlyDictionary<string, string> propertyEquals)
    {
        var index = 0;
        foreach (var (key, expected) in propertyEquals)
        {
            var name = $"@{prefix}{index++}";
            command.Parameters.AddWithValue(name, expected);
            sql.Append(" AND LOWER(").Append(JsonScalar(column, key)).Append(") = LOWER(").Append(name).Append(')');
        }
    }

    private static IReadOnlyDictionary<string, string?> ReadProperties(
        DbDataReader reader,
        int firstOrdinal,
        IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            return EmptyLinkProperties;

        var properties = new Dictionary<string, string?>(keys.Count, StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            var ordinal = firstOrdinal + i;
            properties[keys[i]] = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        return properties;
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyLinkProperties =
        new Dictionary<string, string?>();
}
