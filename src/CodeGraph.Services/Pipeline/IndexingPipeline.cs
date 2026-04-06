using System.Diagnostics;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    private readonly IGraphStore _store;
    private readonly IEnumerable<ICodeExtractor> _extractors;
    private readonly IndexingOptions _options;
    private readonly ILogger<IndexingPipeline> _logger;
    private readonly ISolutionAnalyzer? _solutionAnalyzer;
    private readonly INuGetReferenceExtractor? _nugetExtractor;
    private readonly ITypeScriptAnalyzer? _typeScriptAnalyzer;
    private readonly IFileSystem _fileSystem;
    private readonly string[] _foundationalRepos;

    public IndexingPipeline(
        IGraphStore store,
        IEnumerable<ICodeExtractor> extractors,
        IndexingOptions options,
        IFileSystem fileSystem,
        ILogger<IndexingPipeline> logger,
        ISolutionAnalyzer? solutionAnalyzer = null,
        INuGetReferenceExtractor? nugetExtractor = null,
        ITypeScriptAnalyzer? typeScriptAnalyzer = null)
    {
        _store = store;
        _extractors = extractors;
        _options = options;
        _fileSystem = fileSystem;
        _logger = logger;
        _solutionAnalyzer = solutionAnalyzer;
        _nugetExtractor = nugetExtractor;
        _typeScriptAnalyzer = typeScriptAnalyzer;
        _foundationalRepos = _options.FoundationalRepos ?? [];
    }

    public async Task IndexProjectAsync(string projectName, string rootPath,
        FoundationalKnowledge? knowledge = null,
        IReadOnlyList<string>? changedFilesOnly = null,
        string? repoUrl = null,
        string? sourceGroup = null,
        CancellationToken ct = default)
    {
        var pipelineSw = Stopwatch.StartNew();
        _logger.LogInformation("Indexing {Project} at {Path}", projectName, rootPath);

        var isFoundational = _foundationalRepos.Contains(projectName, StringComparer.OrdinalIgnoreCase);

        // Ensure repository row exists before inserting nodes (FK constraint)
        // Language/framework will be updated after extraction (detected by extractors)
        await _store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = projectName,
            LocalPath = rootPath,
            RepoUrl = repoUrl,
            SourceGroup = sourceGroup,
            IsFoundational = isFoundational
        });

        var buffer = new GraphBuffer();
        ProjectMetadata? detectedMetadata = null;
        var context = new ExtractorContext
        {
            ProjectName = projectName,
            RootPath = rootPath,
            FoundationalKnowledge = knowledge
        };

        // Load existing file hashes for incremental indexing
        var existingHashes = await _store.GetFileHashesAsync(projectName);

        // Phase 1 — Discovery + Extraction
        var stepSw = Stopwatch.StartNew();
        var files = DiscoverFiles(rootPath, changedFilesOnly);
        var filesToProcess = FilterByHash(files, rootPath, existingHashes, buffer);
        _logger.LogInformation("[Timing] Discovery + hashing: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);

        // If no files changed but the project has no extraction nodes (e.g. extractor was
        // just added/fixed), force a full re-extraction by clearing hashes.
        if (filesToProcess.Count == 0 && files.Count > 0)
        {
            var existingNodes = await _store.GetAllNodesByProjectAsync(projectName);
            var hasExtractionNodes = existingNodes.Any(n =>
                n.Label is not ("Repository" or "DotnetProject" or "Folder" or "File"));
            if (!hasExtractionNodes)
            {
                _logger.LogInformation("No extraction nodes found for {Project} despite {FileCount} files — forcing full re-extraction",
                    projectName, files.Count);
                filesToProcess = files;
            }
        }

        _logger.LogInformation("Found {Total} files, {Changed} changed",
            files.Count, filesToProcess.Count);

        // Discover .csproj files once and reuse for structural nodes + NuGet extraction
        var csprojFiles = _fileSystem.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).ToArray();

        // Pass 1: Structural nodes (Project, Folder, File)
        stepSw.Restart();
        CreateStructuralNodes(projectName, rootPath, files, csprojFiles, buffer);
        _logger.LogInformation("[Timing] Structural nodes: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);

        // Pass 2: Extract code elements using specialized analyzers where available.
        // Track which extensions have been handled so per-file extraction skips them.
        var specializedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // C# — solution-level Roslyn analysis
        if (_solutionAnalyzer is not null)
        {
            var slnFiles = _fileSystem.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).ToArray();
            if (slnFiles.Length > 0)
            {
                _logger.LogInformation("Using solution-level Roslyn analysis for {Sln}",
                    Path.GetFileName(slnFiles[0]));
                stepSw.Restart();
                try
                {
                    var results = await _solutionAnalyzer.AnalyzeSolutionAsync(slnFiles[0], context, ct);
                    _logger.LogInformation("[Timing] Roslyn solution analysis: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);
                    detectedMetadata ??= ExtractMetadata(results);
                    MergeResults(results, buffer);
                    specializedExtensions.Add(".cs");
                }
                // Broad catch is intentional: Roslyn can throw many exception types
                // (ReflectionTypeLoadException, BadImageFormatException, etc.) and we must
                // always fall back gracefully to per-file extraction.
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Roslyn solution analysis failed for {Sln} — falling back to per-file extraction",
                        Path.GetFileName(slnFiles[0]));
                }
            }
        }

        // TypeScript/Angular — Node.js sidecar analysis
        if (_typeScriptAnalyzer is not null)
        {
            // The sidecar now scans files from disk (ignoring tsconfig include/files)
            // so we only need one tsconfig per repo — use the root one for compiler options.
            var rootTsconfig = Path.Combine(rootPath, "tsconfig.json");
            var tsconfigFiles = _fileSystem.FileExists(rootTsconfig)
                ? new[] { rootTsconfig }
                : _fileSystem.EnumerateFiles(rootPath, "tsconfig.json", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
                    .Take(1)
                    .ToArray();

            foreach (var tsconfig in tsconfigFiles)
            {
                _logger.LogInformation("Using TypeScript project analysis for {Tsconfig}",
                    Path.GetRelativePath(rootPath, tsconfig));
                stepSw.Restart();
                try
                {
                    var results = await _typeScriptAnalyzer.AnalyzeProjectAsync(
                        tsconfig, context, ct);
                    _logger.LogInformation("[Timing] TypeScript project analysis: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);
                    detectedMetadata ??= ExtractMetadata(results);
                    MergeResults(results, buffer);
                    specializedExtensions.Add(".ts");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "TypeScript project analysis failed for {Tsconfig} — falling back to per-file extraction",
                        Path.GetRelativePath(rootPath, tsconfig));
                }
            }
        }

        // Per-file extraction for everything not handled by a specialized analyzer
        var remainingFiles = specializedExtensions.Count > 0
            ? filesToProcess.Where(f => !specializedExtensions.Contains(
                Path.GetExtension(f))).ToList()
            : filesToProcess;

        if (remainingFiles.Count > 0)
        {
            stepSw.Restart();
            var perFileMetadata = await ExtractFilesAsync(remainingFiles, rootPath, context, buffer, ct);
            _logger.LogInformation("[Timing] Per-file extraction ({FileCount} files): {ElapsedMs}ms", remainingFiles.Count, stepSw.ElapsedMilliseconds);
            detectedMetadata ??= perFileMetadata;
        }

        // Extract NuGet package references from .csproj files (reuse cached discovery)
        if (_nugetExtractor is not null)
            ExtractNuGetReferences(projectName, csprojFiles, buffer);

        // Update repository with language/framework detected by extractors
        if (detectedMetadata is not null)
        {
            await _store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = projectName,
                Language = detectedMetadata.Language,
                Framework = detectedMetadata.Framework
            });
        }

        // Phase 2 — Resolution
        stepSw.Restart();
        _logger.LogInformation("Pre-resolution: {Nodes} nodes, {PendingEdges} pending edges, {UnresolvedCalls} unresolved calls",
            buffer.AllNodes.Count, buffer.AllPendingEdges.Count, buffer.AllUnresolvedCalls.Count);
        ResolveImports(buffer);
        ResolveCalls(buffer);
        CreateStubNodesForExternalTargets(projectName, buffer);
        _logger.LogInformation("[Timing] Resolution phase: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);

        // Phase 3 — Flush
        stepSw.Restart();
        var qnToId = await _store.UpsertNodeBatchAsync(buffer.AllNodes.ToList(), ct);
        _logger.LogInformation("[Timing] Node upsert ({NodeCount} nodes): {ElapsedMs}ms", buffer.AllNodes.Count, stepSw.ElapsedMilliseconds);

        stepSw.Restart();
        var resolvedEdges = buffer.ResolveEdges(projectName, qnToId, _logger);
        await _store.InsertEdgeBatchAsync(resolvedEdges, ct);
        _logger.LogInformation("[Timing] Edge resolution + insert ({EdgeCount} edges): {ElapsedMs}ms", resolvedEdges.Count, stepSw.ElapsedMilliseconds);

        stepSw.Restart();
        await _store.UpsertFileHashBatchAsync(projectName,
            buffer.AllFileHashes.ToDictionary(kv => kv.Key, kv => kv.Value), ct);
        _logger.LogInformation("[Timing] File hash upsert: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);

        pipelineSw.Stop();
        _logger.LogInformation("Indexed {Project}: {Nodes} nodes, {Edges} edges in {TotalMs}ms",
            projectName, buffer.AllNodes.Count, resolvedEdges.Count, pipelineSw.ElapsedMilliseconds);
    }

    // ── File Discovery & Hashing ─────────────────────────────────────────

    private List<string> DiscoverFiles(string rootPath,
        IReadOnlyList<string>? changedFilesOnly)
    {
        if (changedFilesOnly != null)
            return changedFilesOnly
                .Select(f => Path.Combine(rootPath, f))
                .Where(_fileSystem.FileExists)
                .ToList();

        var matcher = new Matcher();
        matcher.AddInclude("**/*");
        foreach (var skip in _options.SkipPatterns)
            matcher.AddExclude(skip);

        var supportedExtensions = _extractors
            .SelectMany(e => e.SupportedExtensions)
            .ToHashSet();

        return matcher.GetResultsInFullPath(rootPath)
            .Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();
    }

    private List<string> FilterByHash(List<string> files, string rootPath,
        Dictionary<string, string> existingHashes, GraphBuffer buffer)
    {
        var changed = new List<string>();
        foreach (var file in files)
        {
            var relPath = Path.GetRelativePath(rootPath, file);
            var hash = ComputeHash(file);
            buffer.AddFileHash(relPath, hash);

            if (!existingHashes.TryGetValue(relPath, out var existing) ||
                existing != hash)
            {
                changed.Add(file);
            }
        }
        return changed;
    }

    private string ComputeHash(string filePath)
    {
        var bytes = _fileSystem.ReadAllBytes(filePath);
        var hash = System.IO.Hashing.XxHash3.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    // ── Per-File Extraction ──────────────────────────────────────────────

    private async Task<ProjectMetadata?> ExtractFilesAsync(List<string> files, string rootPath,
        ExtractorContext context, GraphBuffer buffer, CancellationToken ct)
    {
        ProjectMetadata? metadata = null;

        await Parallel.ForEachAsync(files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxParallelFiles,
                CancellationToken = ct
            },
            async (filePath, ct2) =>
            {
                var ext = Path.GetExtension(filePath);
                var extractor = _extractors.FirstOrDefault(e =>
                    e.SupportedExtensions.Contains(ext));
                if (extractor is null) return;

                try
                {
                    var content = await _fileSystem.ReadAllTextAsync(filePath, ct2);

                    // Skip files over size limit
                    if (content.Length > _options.MaxFileSizeKb * 1024) return;

                    var result = await extractor.ExtractAsync(filePath, content,
                        context, ct2);

                    foreach (var node in result.Nodes)
                        buffer.AddNode(node);
                    foreach (var edge in result.Edges)
                        buffer.AddEdge(edge);
                    foreach (var call in result.UnresolvedCalls)
                        buffer.AddUnresolvedCall(call);
                    foreach (var import in result.UnresolvedImports)
                        buffer.AddUnresolvedImport(import);

                    if (result.Metadata is not null)
                        Interlocked.CompareExchange(ref metadata, result.Metadata, null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract {File}", filePath);
                    // Continue — don't let one file break the pipeline
                }
            });

        return metadata;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void MergeResults(IReadOnlyList<ExtractionResult> results, GraphBuffer buffer)
    {
        foreach (var result in results)
        {
            foreach (var node in result.Nodes) buffer.AddNode(node);
            foreach (var edge in result.Edges) buffer.AddEdge(edge);
            foreach (var call in result.UnresolvedCalls) buffer.AddUnresolvedCall(call);
            foreach (var import in result.UnresolvedImports) buffer.AddUnresolvedImport(import);
        }
    }

    /// <summary>
    /// Extract the first non-null ProjectMetadata from a set of extraction results.
    /// </summary>
    private static ProjectMetadata? ExtractMetadata(IReadOnlyList<ExtractionResult> results) =>
        results.Select(r => r.Metadata).FirstOrDefault(m => m is not null);
}
