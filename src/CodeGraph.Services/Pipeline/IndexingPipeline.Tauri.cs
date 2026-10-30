using CodeGraph.Models;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    /// <summary>Runs after source extraction and before resolution/slice assembly. Only files
    /// already admitted by discovery may be read. Returns additional real source slices whose
    /// Tauri facts changed, restoring their other persisted facts when source was unchanged.</summary>
    private async Task<IReadOnlyList<string>> PrepareTauriBindingsAsync(string repository, string rootPath,
        IReadOnlyList<string> admittedScope, GraphBuffer buffer, IReadOnlySet<string> freshlyProcessedFiles,
        IReadOnlySet<string> deletedFiles, bool incremental, CancellationToken ct)
    {
        var oldEdges = incremental
            ? await _store.FindEdgesByPropertyAsync(repository,
                [EdgeType.CALLS, EdgeType.SUBSCRIBES, EdgeType.PUBLISHES, EdgeType.HANDLES], "extraction_owner", [TauriOperationAnalysis.Owner], ct)
            : [];
        var configurations = admittedScope.Where(IsTauriConfiguration).ToList();
        if (configurations.Count == 0 && oldEdges.Count == 0) return [];
        var roots = configurations.Select(path => NormalizeRelativePath(Path.GetDirectoryName(path) ?? "")).ToList();
        bool InApp(string path) => roots.Any(root => root.Length == 0 || path.StartsWith(root + "/", StringComparison.Ordinal));
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in admittedScope.Where(path => IsTauriConfiguration(path)
                     || path.EndsWith("package.json", StringComparison.Ordinal) || path.EndsWith("Cargo.toml", StringComparison.Ordinal)
                     || path.EndsWith(".rs", StringComparison.Ordinal) && InApp(path)))
        {
            ct.ThrowIfCancellationRequested();
            try { sources[path] = _fileSystem.ReadAllText(Path.Combine(rootPath, path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException($"Tauri analysis incomplete: could not read admitted source {path}; prior graph and hashes are preserved.", ex); }
        }
        // Query only potentially relevant app source slices, never hydrate the repository's
        // Rust graph. Exact function file/span matching happens inside the pure analyzer.
        var candidatePaths = sources.Where(pair => pair.Key.EndsWith(".rs", StringComparison.Ordinal)
                && (pair.Value.Contains("command", StringComparison.Ordinal) || pair.Value.Contains(".emit", StringComparison.Ordinal)))
            .Select(pair => pair.Key).ToList();
        var oldPaths = oldEdges.SelectMany(edge => new[] { edge.SourceFilePath, edge.TargetFilePath,
                TypeScriptNodeText(edge.Properties.GetValueOrDefault("extraction_source_file")) })
            .OfType<string>().Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lookupPaths = candidatePaths.Concat(oldPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var persistedNodes = new List<GraphNode>();
        if (incremental)
            foreach (var batch in lookupPaths.Chunk(500)) persistedNodes.AddRange(await _store.FindNodesByFilesAsync(repository, batch, ct));
        var declarations = buffer.AllNodes.Concat(persistedNodes.Where(node => !freshlyProcessedFiles.Contains(node.FilePath)
                && !deletedFiles.Contains(node.FilePath)))
            .DistinctBy(node => node.QualifiedName).ToList();
        var references = buffer.AllNodes.Where(node => TypeScriptNodeText(node.Properties.GetValueOrDefault("kind")) is
            "tauri_command_reference" or "tauri_event_reference").ToList();
        var analysis = TauriOperationAnalysis.Analyze(repository, sources, declarations, references);
        foreach (var diagnostic in analysis.Diagnostics) _logger.LogInformation("{Diagnostic}", diagnostic);
        if (!analysis.Result.Complete)
            throw new InvalidOperationException("Tauri analysis incomplete; prior graph and source hashes are preserved. "
                + string.Join("; ", analysis.Diagnostics.Where(diagnostic => diagnostic.StartsWith("tauriIncomplete:")).Take(10)));
        var current = Signatures(repository, analysis.Result.Nodes, analysis.Result.Edges);
        var previous = Signatures(repository, persistedNodes.Where(node => IsTauriOwned(node.Properties)),
            oldEdges.Select(edge => new PendingEdge(edge.SourceQN, edge.TargetQN, edge.Type, edge.Properties)));
        var changed = incremental ? current.Keys.Union(previous.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !current.TryGetValue(path, out var next) || !previous.TryGetValue(path, out var old) || !next.SetEquals(old))
            .Order(StringComparer.Ordinal).ToList() : current.Keys.ToList();

        // Backend-only registration/event changes can replace otherwise unchanged Rust or web
        // source slices. Restore their complete non-Tauri outgoing facts, then add fresh native
        // facts; a removed command must not remove the actual Rust function's other dependencies.
        var restorePaths = changed.Where(path => !freshlyProcessedFiles.Contains(path) && !deletedFiles.Contains(path)).ToList();
        var restoreNodes = persistedNodes.Where(node => restorePaths.Contains(node.FilePath, StringComparer.OrdinalIgnoreCase)).ToList();
        var missingPaths = restorePaths.Except(restoreNodes.Select(node => node.FilePath), StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var batch in missingPaths.Chunk(500)) restoreNodes.AddRange(await _store.FindNodesByFilesAsync(repository, batch, ct));
        var byId = restoreNodes.DistinctBy(node => node.Id).ToDictionary(node => node.Id);
        var restoreEdges = new List<GraphEdge>();
        foreach (var batch in byId.Keys.Chunk(500)) restoreEdges.AddRange(await _store.FindEdgesBySourceBatchAsync(batch));
        foreach (var batch in restoreEdges.Select(edge => edge.TargetId).Where(id => !byId.ContainsKey(id)).Distinct().Chunk(500))
            foreach (var (id, node) in await _store.FindNodesByIdBatchAsync(batch)) byId[id] = node;
        foreach (var node in restoreNodes.Where(node => !IsTauriOwned(node.Properties)))
            if (buffer.FindByQN(node.QualifiedName) is null) buffer.AddNode(node);
        foreach (var edge in restoreEdges.Where(edge => !IsTauriOwned(edge.Properties)))
            if (byId.TryGetValue(edge.SourceId, out var source) && byId.TryGetValue(edge.TargetId, out var target)
                && !deletedFiles.Contains(target.FilePath))
                buffer.AddEdge(new PendingEdge(source.QualifiedName, target.QualifiedName, edge.Type, edge.Properties));
        foreach (var node in analysis.Result.Nodes) buffer.AddNode(node);
        foreach (var edge in analysis.Result.Edges) buffer.AddEdge(edge);
        _logger.LogInformation("Tauri semantic refresh of {Repository}: {Count} changed slice(s)", repository, changed.Count);
        return changed;
    }

    private static bool IsTauriOwned(Dictionary<string, object>? properties) => properties is not null
        && TypeScriptNodeText(properties.GetValueOrDefault("extraction_owner")) == TauriOperationAnalysis.Owner;
    private static bool IsTauriConfiguration(string path) => Path.GetFileName(path) is "tauri.conf.json" or "tauri.conf.json5" or "Tauri.toml";
}
