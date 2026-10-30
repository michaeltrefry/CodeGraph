using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.TypeScript;

public class TypeScriptProjectAnalyzer : ITypeScriptAnalyzer
{
    /// <summary>
    /// Bump when extraction output (including the Node.js sidecar's) changes for unchanged input
    /// (see ExtractionVersionCatalog).
    /// </summary>
    /// <remarks>
    /// 2 (sc-26145): the sidecar reads only the files discovery admits (no agent-worktree or
    /// dependency-directory copies), and type text in INJECTS targets and node properties renders
    /// <c>import("…")</c> paths repo-relative instead of with the absolute checkout path.
    /// 3 (sc-26145): same-named workspace packages get directory-qualified Module qualified names
    /// and an import of such a name binds the importer's nearest package.
    /// 4 (sc-27973): JS/JSX components, lexical callables, rendering/context and explicit
    /// extraction ownership/completeness with default configuration and full source accounting.
    /// 5 (sc-27972): framework routes/templates/providers, HTTP wrappers, Tauri references,
    /// declared screen keys and exact public package-export identities.
    /// 6 (sc-27972): authoritative scoped source coverage, relative ApiContract endpoints
    /// and alias-aware static request values.
    /// </remarks>
    public string ExtractionVersion => "10"; // Early weighted AST and core-output allocation bounds.

    private readonly TypeScriptServerManager _server;
    private readonly ILogger<TypeScriptProjectAnalyzer> _logger;

    public TypeScriptProjectAnalyzer(
        TypeScriptServerManager server,
        ILogger<TypeScriptProjectAnalyzer> logger)
    {
        _server = server;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string tsconfigPath,
        ExtractorContext context,
        IReadOnlyList<string> includedFiles,
        CancellationToken ct = default)
    {
        TypeScriptSourceLimits.ValidateScope(includedFiles, ct);
        if (!await _server.EnsureStartedAsync(ct))
            return [];

        _logger.LogInformation(
            "Extracting TypeScript repository {Repository} via Node.js sidecar", context.RepositoryName);

        // The scope of a large repository (tens of thousands of paths) goes through a temp file,
        // so it never meets the sidecar's request body limit.
        var scopePath = Path.Combine(Path.GetTempPath(), $"codegraph-ts-scope-{Guid.NewGuid():N}.json");
        ExtractProjectResponse? response;
        try
        {
            await File.WriteAllTextAsync(scopePath, JsonSerializer.Serialize(includedFiles), ct);
            response = await _server.ExtractProjectAsync(new ExtractProjectRequest
            {
                RepositoryName = context.RepositoryName,
                RootPath = context.RootPath,
                TsconfigPath = tsconfigPath,
                IncludedFilesPath = scopePath,
                MaxFileSizeKb = context.TypeScriptSourceLimits.MaxFileSizeKb,
                MaxFileSizeKbByExtension = context.TypeScriptSourceLimits.MaxFileSizeKbByExtension,
                MaxAnalysisTextKb = context.TypeScriptSourceLimits.MaxAnalysisTextKb,
            }, ct);
        }
        finally
        {
            try { File.Delete(scopePath); } catch (IOException) { /* best effort */ }
        }

        // A failed request is not an empty project: the pipeline preserves the prior graph.
        if (response is null)
        {
            throw new InvalidOperationException(
                $"The TypeScript sidecar returned no extraction result for {context.RepositoryName} (see the preceding warning for the cause).");
        }

        if (!response.Complete)
            throw new InvalidOperationException($"TypeScript analysis of {context.RepositoryName} is incomplete; retaining the previous graph and hashes. {string.Join("; ", response.Diagnostics?.Take(5) ?? [])}");

        // Framework evidence comes from admitted configuration and extracted source metadata.
        var hasAngular = includedFiles.Any(path => Path.GetFileName(path) == "angular.json")
            || response.Nodes.Any(node => PropertyText(node.Properties.GetValueOrDefault("framework")) == "angular");
        var hasReact = response.Nodes.Any(node => PropertyText(node.Properties.GetValueOrDefault("framework")) == "react");
        var language = includedFiles.Any(path => path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)) ? "TypeScript" : "JavaScript";
        var metadata = new ProjectMetadata(language, hasAngular ? "Angular" : hasReact ? "React" : "Node.js");

        var result = new ExtractionResult
        {
            Nodes = response.Nodes
                .Select(n => ToGraphNode(n, context.RepositoryName))
                .ToList(),
            Edges = response.Edges
                .Select(ToPendingEdge)
                .ToList(),
            UnresolvedImports = response.UnresolvedImports
                .Select(i => new UnresolvedImport(i.FileQN, i.ImportedNamespace))
                .ToList(),
            UnresolvedCalls = response.UnresolvedCalls
                .Select(c => new UnresolvedCall(
                    c.CallerQN,
                    c.CalleeName,
                    c.ReceiverType,
                    c.Confidence,
                    c.ReceiverType is null ? CallReceiverKind.Unknown : CallReceiverKind.Resolved))
                .ToList(),
            ProcessedFiles = response.ProcessedFiles,
            Metadata = metadata,
        };

        if (response.Diagnostics is { Count: > 0 })
        {
            foreach (var diag in response.Diagnostics)
                _logger.LogInformation("TS diag: {Diag}", diag);
        }

        _logger.LogInformation(
            "TypeScript extraction complete: {Nodes} nodes, {Edges} edges, {WorkspacePackages} workspace packages, {ResolvedImports} resolved imports",
            result.Nodes.Count, result.Edges.Count, response.WorkspacePackages.Count, response.ResolvedImports.Count);

        return [result];
    }

    private static string? PropertyText(object? value) => value is JsonElement json && json.ValueKind == JsonValueKind.String
        ? json.GetString() : value?.ToString();

    private static GraphNode ToGraphNode(GraphNodeDto dto, string repositoryName) => new()
    {
        Repository = repositoryName,
        Label = Enum.Parse<NodeLabel>(dto.Label),
        Name = dto.Name,
        QualifiedName = dto.QualifiedName,
        FilePath = dto.FilePath,
        StartLine = dto.StartLine,
        EndLine = dto.EndLine,
        Properties = dto.Properties,
    };

    private static PendingEdge ToPendingEdge(PendingEdgeDto dto) =>
        new(dto.SourceQN, dto.TargetQN, Enum.Parse<EdgeType>(dto.Type), dto.Properties);
}
