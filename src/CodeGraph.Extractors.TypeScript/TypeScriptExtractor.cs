using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.TypeScript;

/// <summary>
/// File-scoped JS/TS extractor, including branch overlays and standalone callers.
/// TypeScriptProjectAnalyzer otherwise owns admitted JS/JSX/TS/TSX with optional configuration.
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
        new HashSet<string> { ".js", ".jsx", ".ts", ".tsx" };

    /// <summary>
    /// Bump when extraction output (including the Node.js sidecar's) changes for unchanged input
    /// (see ExtractionVersionCatalog).
    /// </summary>
    /// <remarks>
    /// 2 (sc-26145): type text renders <c>import("…")</c> paths relative to the extraction
    /// directory instead of with the (random, per-run) absolute temp path.
    /// 3 (sc-27973): shared callable/component sidecar output and explicit completeness.
    /// 4 (sc-27972): frontend extensions and module-relative file-scoped identities.
    /// 5 (sc-27972): complete admitted coverage and conservative aliased request values.
    /// </remarks>
    public string GetExtractionVersion(string extension) => "6"; // Shared bounded sidecar input.

    public async Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default)
    {
        if (content.Length > Math.Max(0L, (long)context.TypeScriptSourceLimits.MaxFileSizeKbFor(Path.GetExtension(filePath)) * 1024))
            return ExtractionResult.Failure("Frontend source exceeds its configured text limit; prior facts and hashes must be retained.");

        // Skip definition and test files
        if (filePath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase))
            return new ExtractionResult();

        if (!await _server.EnsureStartedAsync(ct))
            return ExtractionResult.Failure("TypeScript sidecar is unavailable.");

        var relative = Path.GetRelativePath(context.RootPath, filePath).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            return ExtractionResult.Failure("Frontend source must be inside its repository root.");
        // Preserve module identity when parsing the source in an isolated temporary checkout.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codegraph-ts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempTs = Path.Combine(tempDir, relative);
        var tempTsconfig = Path.Combine(tempDir, "tsconfig.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempTs)!);
            await File.WriteAllTextAsync(tempTs, content, ct);
            await File.WriteAllTextAsync(tempTsconfig,
                """{"compilerOptions":{"target":"ES2020","module":"commonjs","allowJs":true,"jsx":"preserve"},"include":["**/*.ts","**/*.tsx","**/*.js","**/*.jsx"]}""",
                ct);

            var response = await _server.ExtractProjectAsync(new ExtractProjectRequest
            {
                RepositoryName = context.RepositoryName,
                RootPath = tempDir,
                TsconfigPath = tempTsconfig,
                IncludedFiles = [relative, Path.GetFileName(tempTsconfig)],
                MaxFileSizeKb = context.TypeScriptSourceLimits.MaxFileSizeKb,
                MaxFileSizeKbByExtension = context.TypeScriptSourceLimits.MaxFileSizeKbByExtension,
                MaxAnalysisTextKb = context.TypeScriptSourceLimits.MaxAnalysisTextKb,
            }, ct);

            if (response is null)
                return ExtractionResult.Failure("TypeScript sidecar returned no response.");
            if (!response.Complete)
                return ExtractionResult.Failure("TypeScript sidecar returned incomplete analysis; prior facts and hashes must be retained.");

            return new ExtractionResult
            {
                Nodes = response.Nodes
                    .Select(n => new GraphNode
                    {
                        Repository = context.RepositoryName,
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
                    .Select(c => new UnresolvedCall(
                        c.CallerQN,
                        c.CalleeName,
                        c.ReceiverType,
                        c.Confidence,
                        c.ReceiverType is null ? CallReceiverKind.Unknown : CallReceiverKind.Resolved))
                    .ToList(),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeScript per-file extraction failed for {File}", filePath);
            return ExtractionResult.Failure(ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
