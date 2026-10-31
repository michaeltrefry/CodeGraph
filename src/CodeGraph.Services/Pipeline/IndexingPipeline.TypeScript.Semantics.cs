using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    private const string TypeScriptOwner = "typescript";

    // Apply the same ownership contract to older analyzers and test adapters. Ownership belongs
    // to extraction facts, not to subsequently resolved/linker-generated relationships.
    private static ExtractionResult OwnTypeScriptResult(ExtractionResult result) => result with
    {
        Nodes = result.Nodes.Select(node => node with
        {
            FilePath = TypeScriptNodeText(node.Properties.GetValueOrDefault("extraction_source_file")) ?? node.FilePath,
            Properties = Owned(node.Properties)
        }).ToList(),
        Edges = result.Edges.Select(edge => edge with { Properties = Owned(edge.Properties) }).ToList()
    };

    private static Dictionary<string, object> Owned(Dictionary<string, object>? properties)
    {
        var owned = properties is null ? new Dictionary<string, object>() : new(properties);
        owned["extraction_owner"] = TypeScriptOwner;
        return owned;
    }

    private static bool IsTypeScriptOwned(Dictionary<string, object>? properties) =>
        properties is not null && TypeScriptNodeText(properties.GetValueOrDefault("extraction_owner")) == TypeScriptOwner;

    private static string ExtractionSlice(GraphNode node) => NormalizeRelativePath(
        TypeScriptNodeText(node.Properties.GetValueOrDefault("extraction_source_file")) ?? node.FilePath);

    /// <summary>
    /// Compare complete raw extraction with persisted extraction-owned facts, before ID/stub
    /// resolution. Outgoing relationships belong to their source slice, so changes in imported
    /// declarations also refresh callers whose bytes have not changed. Linker enrichment is not
    /// part of this comparison. No label or edge-type allow-list limits future framework passes.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindChangedTypeScriptSemanticSlicesAsync(
        string repository, IReadOnlyList<ExtractionResult> results, CancellationToken ct)
    {
        // Only frontend source slices and package modules can contain this analyzer's facts.
        // Native backends can be much larger than the frontend; do not hydrate their graphs.
        var paths = (await _store.GetNodeFilePathsAsync(repository, ct))
            .Where(path => IsTypeScriptPath(path) || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || path == StaleWorkspaceModuleSlice).ToList();
        var storedNodes = new List<GraphNode>();
        foreach (var batch in paths.Chunk(1000))
            storedNodes.AddRange(await _store.FindNodesByFilesAsync(repository, batch, ct));
        storedNodes.AddRange(await _store.FindNodesByLabelAsync(repository, NodeLabel.Module, int.MaxValue));
        storedNodes = storedNodes.DistinctBy(node => node.Id).ToList();
        var ownedStored = storedNodes.Where(node => IsTypeScriptOwned(node.Properties)).ToList();
        var storedEdges = new List<GraphEdge>();
        var sourceIds = storedNodes.Where(node => IsTypeScriptOwned(node.Properties)
            || (node.Label == NodeLabel.File && IsTypeScriptPath(node.FilePath))).Select(node => node.Id);
        foreach (var batch in sourceIds.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();
            storedEdges.AddRange(await _store.FindEdgesBySourceBatchAsync(batch));
        }
        var byId = storedNodes.ToDictionary(node => node.Id);
        var missingEndpoints = storedEdges.Where(edge => IsTypeScriptOwned(edge.Properties))
            .SelectMany(edge => new[] { edge.SourceId, edge.TargetId })
            .Where(id => !byId.ContainsKey(id)).Distinct().ToList();
        foreach (var batch in missingEndpoints.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (id, node) in await _store.FindNodesByIdBatchAsync(batch))
                byId[id] = node;
        }
        var persisted = Signatures(repository, ownedStored, storedEdges
            .Where(edge => IsTypeScriptOwned(edge.Properties) && byId.ContainsKey(edge.SourceId) && byId.ContainsKey(edge.TargetId))
            .Select(edge => new PendingEdge(byId[edge.SourceId].QualifiedName, byId[edge.TargetId].QualifiedName, edge.Type, edge.Properties)));
        var current = Signatures(repository, results.SelectMany(result => result.Nodes), results.SelectMany(result => result.Edges));
        return current.Keys.Union(persisted.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !current.TryGetValue(path, out var next) || !persisted.TryGetValue(path, out var previous) || !next.SetEquals(previous))
            .Order(StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, HashSet<string>> Signatures(string repository, IEnumerable<GraphNode> nodes, IEnumerable<PendingEdge> edges)
    {
        var byQn = nodes.DistinctBy(node => node.QualifiedName).ToDictionary(node => node.QualifiedName, StringComparer.Ordinal);
        var slices = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string value)
        {
            if (string.IsNullOrWhiteSpace(path)) return; // root package identities have their existing module reconciliation
            if (!slices.TryGetValue(path, out var signatures)) slices[path] = signatures = new(StringComparer.Ordinal);
            signatures.Add(value);
        }
        foreach (var node in byQn.Values)
            Add(ExtractionSlice(node), TypeScriptNodeSignature(node));
        foreach (var edge in edges)
        {
            var path = TypeScriptNodeText(edge.Properties?.GetValueOrDefault("extraction_source_file"));
            if (path is null && byQn.TryGetValue(edge.SourceQN, out var source)) path = ExtractionSlice(source);
            // Imports, initializers and top-level rendering can originate at the structural File
            // node, which is created by the pipeline rather than returned by the sidecar.
            var prefix = $"{repository}:";
            if (path is null && edge.SourceQN.StartsWith(prefix, StringComparison.Ordinal)
                && IsTypeScriptPath(edge.SourceQN[prefix.Length..]))
                path = edge.SourceQN[prefix.Length..];
            if (path is not null)
                Add(NormalizeRelativePath(path), JsonSerializer.Serialize(new object[] { "edge", edge.SourceQN, edge.TargetQN, edge.Type.ToString(), SemanticProperties(edge.Properties) }));
        }
        return slices;
    }

    private static string TypeScriptNodeSignature(GraphNode node) => JsonSerializer.Serialize(new object[]
    {
        "node", node.Label.ToString(), node.QualifiedName, node.Name, NormalizeRelativePath(node.FilePath),
        node.StartLine, node.EndLine, SemanticProperties(node.Properties)
    });

    private static readonly HashSet<string> NonSemanticProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "extraction_owner", "extraction_source_file", "description", "analysis", "analysis_confidence", "generated_description",
        "timestamp", "created_at", "updated_at", "indexed_at", "duration_ms", "elapsed_ms", "diagnostics", "id"
    };

    private static string SemanticProperties(Dictionary<string, object>? properties) =>
        Canonical(JsonSerializer.SerializeToNode(properties?.Where(pair => !NonSemanticProperties.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value) ?? []));

    // JSON normalizes dictionary/JsonElement representations across the DB boundary. Fact
    // collections are sets above; property arrays retain order because parameters and provider
    // precedence can be semantically ordered.
    private static string Canonical(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(',', obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(',', array.Select(Canonical)) + "]",
        _ => value.ToJsonString()
    };
}
