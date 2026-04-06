using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.TypeScript;

/// <summary>
/// Per-file fallback extractor for .ts files in repos that have no tsconfig.json.
/// Repos with a tsconfig.json use TypeScriptProjectAnalyzer (project-level) instead.
/// </summary>
public class TypeScriptExtractor : ICodeExtractor
{
    private readonly TypeScriptServerManager _server;
    private readonly ILogger<TypeScriptExtractor> _logger;

    public TypeScriptExtractor(TypeScriptServerManager server, ILogger<TypeScriptExtractor> logger)
    {
        _server = server;
        _logger = logger;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string> { ".ts" };

    public async Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default)
    {
        // Skip definition and test files
        if (filePath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase))
            return new ExtractionResult();

        if (!await _server.EnsureStartedAsync(ct))
            return new ExtractionResult();

        // Write content to a temp file so the server can parse it with minimal context
        var tempDir = Path.Combine(Path.GetTempPath(), $"codegraph-ts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempTs = Path.Combine(tempDir, Path.GetFileName(filePath));
        var tempTsconfig = Path.Combine(tempDir, "tsconfig.json");

        try
        {
            await File.WriteAllTextAsync(tempTs, content, ct);
            await File.WriteAllTextAsync(tempTsconfig,
                """{"compilerOptions":{"target":"ES2020","module":"commonjs"},"include":["*.ts"]}""",
                ct);

            var response = await _server.ExtractProjectAsync(new ExtractProjectRequest
            {
                ProjectName = context.ProjectName,
                RootPath = tempDir,
                TsconfigPath = tempTsconfig,
            }, ct);

            if (response is null) return new ExtractionResult();

            return new ExtractionResult
            {
                Nodes = response.Nodes
                    .Select(n => new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = Enum.Parse<NodeLabel>(n.Label),
                        Name = n.Name,
                        QualifiedName = n.QualifiedName,
                        FilePath = Path.GetRelativePath(context.RootPath, filePath),
                        StartLine = n.StartLine,
                        EndLine = n.EndLine,
                        Properties = n.Properties,
                    })
                    .ToList(),
                Edges = response.Edges
                    .Select(e => new PendingEdge(e.SourceQN, e.TargetQN,
                        Enum.Parse<EdgeType>(e.Type), e.Properties))
                    .ToList(),
                UnresolvedImports = response.UnresolvedImports
                    .Select(i => new UnresolvedImport(i.FileQN, i.ImportedNamespace))
                    .ToList(),
                UnresolvedCalls = response.UnresolvedCalls
                    .Select(c => new UnresolvedCall(c.CallerQN, c.CalleeName, c.ReceiverType, c.Confidence))
                    .ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeScript per-file extraction failed for {File}", filePath);
            return new ExtractionResult();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
