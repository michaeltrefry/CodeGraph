using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.TypeScript;

public class TypeScriptProjectAnalyzer : ITypeScriptAnalyzer
{
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
        string tsconfigPath, ExtractorContext context, CancellationToken ct = default)
    {
        if (!await _server.EnsureStartedAsync(ct))
            return [];

        _logger.LogInformation(
            "Extracting TypeScript project {Project} via Node.js sidecar", context.ProjectName);

        var response = await _server.ExtractProjectAsync(new ExtractProjectRequest
        {
            ProjectName = context.ProjectName,
            RootPath = context.RootPath,
            TsconfigPath = tsconfigPath,
        }, ct);

        if (response is null) return [];

        // Detect Angular vs plain Node.js from angular.json in root
        var hasAngular = File.Exists(Path.Combine(context.RootPath, "angular.json"));
        var metadata = new ProjectMetadata("TypeScript", hasAngular ? "Angular" : "Node.js");

        var result = new ExtractionResult
        {
            Nodes = response.Nodes
                .Select(n => ToGraphNode(n, context.ProjectName))
                .ToList(),
            Edges = response.Edges
                .Select(ToPendingEdge)
                .ToList(),
            UnresolvedImports = response.UnresolvedImports
                .Select(i => new UnresolvedImport(i.FileQN, i.ImportedNamespace))
                .ToList(),
            UnresolvedCalls = response.UnresolvedCalls
                .Select(c => new UnresolvedCall(c.CallerQN, c.CalleeName, c.ReceiverType, c.Confidence))
                .ToList(),
            Metadata = metadata,
        };

        if (response.Diagnostics is { Count: > 0 })
        {
            foreach (var diag in response.Diagnostics)
                _logger.LogInformation("TS diag: {Diag}", diag);
        }

        _logger.LogInformation(
            "TypeScript extraction complete: {Nodes} nodes, {Edges} edges",
            result.Nodes.Count, result.Edges.Count);

        return [result];
    }

    private static GraphNode ToGraphNode(GraphNodeDto dto, string projectName) => new()
    {
        Project = projectName,
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
