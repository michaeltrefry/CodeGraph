using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Metadata;

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
    private readonly IRustAnalyzer? _rustAnalyzer;
    private readonly ICargoManifestExtractor? _cargoManifestExtractor;
    private readonly IFileSystem _fileSystem;
    private readonly string[] _foundationalRepos;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public IndexingPipeline(
        IGraphStore store,
        IEnumerable<ICodeExtractor> extractors,
        IOptions<IndexingOptions> optionsAccessor,
        IFileSystem fileSystem,
        ILogger<IndexingPipeline> logger,
        ISolutionAnalyzer? solutionAnalyzer = null,
        INuGetReferenceExtractor? nugetExtractor = null,
        ITypeScriptAnalyzer? typeScriptAnalyzer = null,
        IRustAnalyzer? rustAnalyzer = null,
        ICargoManifestExtractor? cargoManifestExtractor = null)
    {
        _store = store;
        _extractors = extractors;
        _options = optionsAccessor.Value;
        _fileSystem = fileSystem;
        _logger = logger;
        _solutionAnalyzer = solutionAnalyzer;
        _nugetExtractor = nugetExtractor;
        _typeScriptAnalyzer = typeScriptAnalyzer;
        _rustAnalyzer = rustAnalyzer;
        _cargoManifestExtractor = cargoManifestExtractor;
        _foundationalRepos = _options.FoundationalRepos ?? [];
    }

    public async Task IndexProjectAsync(string projectName, string rootPath,
        FoundationalKnowledge? knowledge = null,
        IReadOnlyList<string>? changedFilesOnly = null,
        string? repoUrl = null,
        string? sourceGroup = null,
        string? repositoryToolingIdentity = null,
        bool replaceExistingGraph = false,
        SyncStateEntity? replacementSyncState = null,
        CancellationToken ct = default)
    {
        var projectLock = ProjectLocks.GetOrAdd(projectName, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(ct);
        try
        {
            await IndexProjectCoreAsync(projectName, rootPath, knowledge, changedFilesOnly,
                repoUrl, sourceGroup, repositoryToolingIdentity, replaceExistingGraph, replacementSyncState, ct);
        }
        finally
        {
            projectLock.Release();
        }
    }

    private async Task IndexProjectCoreAsync(string projectName, string rootPath,
        FoundationalKnowledge? knowledge,
        IReadOnlyList<string>? changedFilesOnly,
        string? repoUrl,
        string? sourceGroup,
        string? repositoryToolingIdentity,
        bool replaceExistingGraph,
        SyncStateEntity? replacementSyncState,
        CancellationToken ct)
    {
        var pipelineSw = Stopwatch.StartNew();
        _logger.LogInformation("Indexing {Project} at {Path}", projectName, rootPath);

        var isFoundational = _foundationalRepos.Contains(projectName, StringComparer.OrdinalIgnoreCase);

        var repository = new RepositoryEntity
        {
            Name = projectName,
            LocalPath = rootPath,
            RepoUrl = repoUrl,
            SourceGroup = sourceGroup,
            IsFoundational = isFoundational
        };

        // Routine upserts need the repository row before node writes for the MariaDB FK.
        // Replacement creates/updates it inside the graph transaction instead.
        if (!replaceExistingGraph)
            await _store.UpsertRepositoryAsync(repository);

        var buffer = new GraphBuffer();
        var detectedMetadataCandidates = new List<ProjectMetadata>();
        var dotnetToolingTrusted = _options.IsDotnetToolingTrusted(repositoryToolingIdentity);
        var context = new ExtractorContext
        {
            ProjectName = projectName,
            RootPath = rootPath,
            FoundationalKnowledge = knowledge,
            RepositoryToolingTrust = dotnetToolingTrusted
                ? RepositoryToolingTrust.Trusted
                : RepositoryToolingTrust.Untrusted
        };

        var existingHashes = replaceExistingGraph
            ? []
            : await _store.GetFileHashesAsync(projectName);

        // Phase 1 — Discovery + Extraction
        var stepSw = Stopwatch.StartNew();
        var files = DiscoverFiles(rootPath, changedFilesOnly);
        var deletedFiles = GetDeletedFiles(rootPath, changedFilesOnly);
        var languageStats = ComputeLanguageStats(files);
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

        var changedRelativePaths = filesToProcess
            .Select(file => NormalizeRelativePath(Path.GetRelativePath(rootPath, file)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var successfullyProcessedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Discover .csproj files once and reuse for structural nodes + NuGet extraction
        var csprojFiles = _fileSystem.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).ToArray();

        // Pass 1: Structural nodes (Project, Folder, File)
        stepSw.Restart();
        CreateStructuralNodes(projectName, rootPath, files, csprojFiles, buffer);
        _logger.LogInformation("[Timing] Structural nodes: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);

        // Pass 2: Extract code elements using specialized analyzers where available.
        // Track exact files completed by project analyzers. A partial project result must
        // fall back per-file instead of advancing hashes for files it did not process.
        // The absolute-path set deduplicates shared projects/documents across solutions.
        var specializedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // C# — solution-level Roslyn analysis
        if (_solutionAnalyzer is not null && dotnetToolingTrusted)
        {
            var solutionFiles = _fileSystem.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly)
                .Concat(_fileSystem.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)),
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (solutionFiles.Length > 0)
            {
                _logger.LogWarning(
                    "SECURITY-AUDIT: repository {Project} with provider-resolved identity {RepositoryIdentity} is explicitly trusted by CodeGraph:IndexingOptions:TrustedDotnetRepositories; enabling repository-controlled restore and MSBuild solution analysis",
                    projectName,
                    repositoryToolingIdentity);
                foreach (var solutionFile in solutionFiles)
                {
                    _logger.LogInformation("Using solution-level Roslyn analysis for {Solution}",
                        Path.GetFileName(solutionFile));
                    stepSw.Restart();
                    try
                    {
                        var analysis = await _solutionAnalyzer.AnalyzeSolutionAsync(solutionFile, context, ct);
                        _logger.LogInformation(
                            "[Timing] Roslyn solution analysis for {Solution}: {ElapsedMs}ms",
                            Path.GetFileName(solutionFile),
                            stepSw.ElapsedMilliseconds);

                        if (analysis.Metadata is not null)
                            detectedMetadataCandidates.Add(analysis.Metadata);

                        foreach (var document in analysis.Documents)
                        {
                            var documentPath = NormalizeAnalyzedDocumentPath(rootPath, document.FilePath);
                            if (!document.Result.Succeeded)
                                continue;
                            if (!specializedFiles.Add(documentPath))
                                continue;

                            var relativePath = NormalizeRelativePath(
                                Path.GetRelativePath(rootPath, documentPath));
                            var result = document.Result with { ProcessedFiles = [relativePath] };
                            MergeResults([result], buffer);
                            AddSuccessfullyProcessedFiles(
                                [result], changedRelativePaths, successfullyProcessedFiles);
                        }
                    }
                    // Broad catch is intentional: Roslyn can throw many exception types
                    // (ReflectionTypeLoadException, BadImageFormatException, etc.) and we must
                    // always fall back gracefully to per-file extraction.
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Roslyn solution analysis failed for {Solution} — falling back to per-file extraction for uncovered files",
                            Path.GetFileName(solutionFile));
                    }
                }
            }
        }
        else if (_solutionAnalyzer is not null)
        {
            var hasSolution = _fileSystem.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).Any()
                || _fileSystem.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).Any();
            if (hasSolution)
            {
                _logger.LogInformation(
                    "SECURITY-AUDIT: repository {Project} with provider-resolved identity {RepositoryIdentity} is untrusted; restore and MSBuild solution analysis are disabled, using syntax-only C# extraction",
                    projectName,
                    repositoryToolingIdentity);
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
                    AddMetadataCandidates(detectedMetadataCandidates, results);
                    MergeResults(results, buffer);
                    AddSuccessfullyProcessedFiles(results, changedRelativePaths, successfullyProcessedFiles);
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

        // Rust — Cargo/SCIP project analysis when rust-analyzer + SCIP data are available.
        if (_rustAnalyzer is not null)
        {
            var rootCargoManifest = Path.Combine(rootPath, "Cargo.toml");
            var cargoManifests = _fileSystem.FileExists(rootCargoManifest)
                ? new[] { rootCargoManifest }
                : _fileSystem.EnumerateFiles(rootPath, "Cargo.toml", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}target{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                    .Take(1)
                    .ToArray();

            foreach (var cargoManifest in cargoManifests)
            {
                _logger.LogInformation("Using Rust project analysis for {Manifest}",
                    Path.GetRelativePath(rootPath, cargoManifest));
                stepSw.Restart();
                try
                {
                    var results = await _rustAnalyzer.AnalyzeProjectAsync(
                        cargoManifest, context, ct);
                    var preparedResults = PrepareRustSemanticResults(results);
                    _logger.LogInformation("[Timing] Rust project analysis: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);
                    // Materialize and validate the complete semantic result before the
                    // shared graph buffer is touched. A malformed/lazy result therefore
                    // cannot leave a partial Rust graph behind before failing the run.
                    MergeResults(preparedResults, buffer);
                    AddMetadataCandidates(detectedMetadataCandidates, preparedResults);
                    AddSuccessfullyProcessedFiles(
                        preparedResults,
                        changedRelativePaths,
                        successfullyProcessedFiles);
                }
                catch (OperationCanceledException) { throw; }
                catch (RustSemanticIndexingException ex)
                {
                    _logger.LogError(ex,
                        "Rust semantic indexing capability failure {FailureCode} for {Manifest}",
                        ex.FailureCode,
                        Path.GetRelativePath(rootPath, cargoManifest));
                    throw;
                }
                catch (Exception ex)
                {
                    var failure = new RustSemanticIndexingException(
                        "rust_semantic_pipeline_failed",
                        $"Rust semantic indexing failed for manifest " +
                        $"'{Path.GetRelativePath(rootPath, cargoManifest)}' before its results could be committed.",
                        ex);
                    _logger.LogError(failure,
                        "Rust semantic indexing failure {FailureCode} for {Manifest}",
                        failure.FailureCode,
                        Path.GetRelativePath(rootPath, cargoManifest));
                    throw failure;
                }
            }
        }

        // Cargo manifests — package definitions and dependency references for every crate/workspace member.
        if (_cargoManifestExtractor is not null)
        {
            var cargoManifestPaths = _fileSystem
                .EnumerateFiles(rootPath, "Cargo.toml", SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}target{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (cargoManifestPaths.Length > 0)
            {
                try
                {
                    var cargoManifests = cargoManifestPaths.ToDictionary(
                        path => path,
                        path => _fileSystem.ReadAllText(path),
                        StringComparer.OrdinalIgnoreCase);
                    var cargoResult = _cargoManifestExtractor.Extract(cargoManifests, context);
                    MergeResults([cargoResult], buffer);
                    if (cargoResult.Metadata is not null)
                        detectedMetadataCandidates.Add(cargoResult.Metadata);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Cargo manifest extraction failed for {Project}", projectName);
                }
            }
        }

        // Per-file extraction for everything not successfully handled by a specialized analyzer
        var remainingFiles = filesToProcess
            .Where(file => !successfullyProcessedFiles.Contains(
                NormalizeRelativePath(Path.GetRelativePath(rootPath, file))))
            .ToList();

        if (remainingFiles.Count > 0)
        {
            stepSw.Restart();
            var perFileResult = await ExtractFilesAsync(
                remainingFiles, rootPath, context, buffer, replaceExistingGraph, ct);
            _logger.LogInformation("[Timing] Per-file extraction ({FileCount} files): {ElapsedMs}ms", remainingFiles.Count, stepSw.ElapsedMilliseconds);
            detectedMetadataCandidates.AddRange(perFileResult.Metadata);
            successfullyProcessedFiles.UnionWith(perFileResult.ProcessedFiles);
        }

        // Extract NuGet package references from .csproj files (reuse cached discovery)
        if (_nugetExtractor is not null)
            ExtractNuGetReferences(projectName, csprojFiles, buffer, replaceExistingGraph);

        ApplyNodeCounts(languageStats, buffer.AllNodes);
        var detectedMetadata = SelectDominantMetadata(detectedMetadataCandidates, languageStats);

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
        int edgeCount;
        if (replaceExistingGraph)
        {
            edgeCount = await _store.ReplaceProjectGraphAsync(
                projectName,
                buffer.AllNodes.ToList(),
                buffer.AllPendingEdges.ToList(),
                buffer.AllFileHashes,
                BuildRepositorySnapshot(repository, detectedMetadata, languageStats),
                replacementSyncState,
                ct);
            _logger.LogInformation(
                "[Timing] Atomic graph replacement ({NodeCount} nodes, {EdgeCount} edges): {ElapsedMs}ms",
                buffer.AllNodes.Count, edgeCount, stepSw.ElapsedMilliseconds);
        }
        else
        {
            var replacementPaths = successfullyProcessedFiles
                .Concat(deletedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var (replacementNodes, replacementEdges) = BuildIncrementalSlice(
                buffer,
                replacementPaths,
                includeProjectStructure: existingHashes.Count == 0);
            var replacementHashes = buffer.AllFileHashes
                .Where(hash => successfullyProcessedFiles.Contains(hash.Key))
                .ToDictionary(hash => hash.Key, hash => hash.Value, StringComparer.OrdinalIgnoreCase);

            edgeCount = await _store.ReplaceProjectFilesAsync(
                projectName,
                replacementPaths,
                replacementNodes,
                replacementEdges,
                replacementHashes,
                ct);
            _logger.LogInformation(
                "[Timing] Atomic file-slice replacement ({FileCount} files, {NodeCount} nodes, {EdgeCount} edges): {ElapsedMs}ms",
                replacementPaths.Count, replacementNodes.Count, edgeCount, stepSw.ElapsedMilliseconds);
        }

        // Publish detected metadata only after the graph write succeeds.
        if (!replaceExistingGraph && detectedMetadata is not null)
        {
            await _store.UpsertRepositoryAsync(
                BuildRepositorySnapshot(repository, detectedMetadata, languageStats));
        }

        pipelineSw.Stop();
        _logger.LogInformation("Indexed {Project}: {Nodes} nodes, {Edges} edges in {TotalMs}ms",
            projectName, buffer.AllNodes.Count, edgeCount, pipelineSw.ElapsedMilliseconds);
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

    private List<string> GetDeletedFiles(string rootPath, IReadOnlyList<string>? changedFilesOnly)
    {
        if (changedFilesOnly is null)
            return [];

        return changedFilesOnly
            .Where(path => !_fileSystem.FileExists(Path.Combine(rootPath, path)))
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> FilterByHash(List<string> files, string rootPath,
        Dictionary<string, string> existingHashes, GraphBuffer buffer)
    {
        var changed = new List<string>();
        foreach (var file in files)
        {
            var relPath = NormalizeRelativePath(Path.GetRelativePath(rootPath, file));
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

    private async Task<PerFileExtractionResult> ExtractFilesAsync(List<string> files, string rootPath,
        ExtractorContext context, GraphBuffer buffer, bool failOnExtractionError, CancellationToken ct)
    {
        var metadataSeen = new ConcurrentBag<ProjectMetadata>();
        var processedFiles = new ConcurrentBag<string>();
        var failures = new ConcurrentQueue<Exception>();

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

                    if (!result.Succeeded)
                    {
                        var failure = new InvalidOperationException(
                            $"Extractor reported failure for {filePath}: " +
                            (result.FailureReason ?? "no reason provided"));
                        _logger.LogWarning(failure, "Failed to extract {File}", filePath);
                        if (failOnExtractionError)
                            failures.Enqueue(failure);
                        return;
                    }

                    foreach (var node in result.Nodes)
                        buffer.AddNode(node);
                    foreach (var edge in result.Edges)
                        buffer.AddEdge(edge);
                    foreach (var call in result.UnresolvedCalls)
                        buffer.AddUnresolvedCall(call);
                    foreach (var import in result.UnresolvedImports)
                        buffer.AddUnresolvedImport(import);

                    if (result.Metadata is not null)
                        metadataSeen.Add(result.Metadata);
                    processedFiles.Add(NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath)));
                }
                catch (OperationCanceledException) when (ct2.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract {File}", filePath);
                    if (failOnExtractionError)
                        failures.Enqueue(new InvalidOperationException($"Failed to extract {filePath}", ex));
                }
            });

        if (!failures.IsEmpty)
            throw new AggregateException("One or more files failed during replacement indexing.", failures);

        return new PerFileExtractionResult(metadataSeen.ToList(), processedFiles.ToList());
    }

    private RepositoryEntity BuildRepositorySnapshot(
        RepositoryEntity repository,
        ProjectMetadata? metadata,
        IReadOnlyDictionary<string, LanguageStatAccumulator> languageStats)
    {
        repository.Language = metadata?.Language;
        repository.Framework = metadata?.Framework;
        repository.Properties = metadata is null
            ? null
            : SerializeRepositoryProperties(metadata, languageStats);
        return repository;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void MergeResults(IReadOnlyList<ExtractionResult> results, GraphBuffer buffer)
    {
        foreach (var result in results.Where(result => result.Succeeded))
        {
            foreach (var node in result.Nodes) buffer.AddNode(node);
            foreach (var edge in result.Edges) buffer.AddEdge(edge);
            foreach (var call in result.UnresolvedCalls) buffer.AddUnresolvedCall(call);
            foreach (var import in result.UnresolvedImports) buffer.AddUnresolvedImport(import);
        }
    }

    private static void AddMetadataCandidates(List<ProjectMetadata> candidates, IReadOnlyList<ExtractionResult> results) =>
        candidates.AddRange(results
            .Where(result => result.Succeeded)
            .Select(result => result.Metadata)
            .Where(metadata => metadata is not null)
            .Cast<ProjectMetadata>());

    private static void AddSuccessfullyProcessedFiles(
        IEnumerable<ExtractionResult> results,
        IReadOnlySet<string> changedFiles,
        ISet<string> successfullyProcessedFiles)
    {
        foreach (var file in results
                     .Where(result => result.Succeeded)
                     .SelectMany(result => result.ProcessedFiles))
        {
            var normalized = NormalizeRelativePath(file);
            if (changedFiles.Contains(normalized))
                successfullyProcessedFiles.Add(normalized);
        }
    }

    private static (List<GraphNode> Nodes, List<PendingEdge> Edges) BuildIncrementalSlice(
        GraphBuffer buffer,
        IReadOnlyList<string> replacementPaths,
        bool includeProjectStructure)
    {
        var paths = replacementPaths
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replacementNodes = buffer.AllNodes
            .Where(node => includeProjectStructure ||
                           (!string.IsNullOrWhiteSpace(node.FilePath) &&
                            paths.Contains(NormalizeRelativePath(node.FilePath))))
            .ToList();
        var replacementQns = replacementNodes
            .Select(node => node.QualifiedName)
            .ToHashSet(StringComparer.Ordinal);
        var selectedEdges = buffer.AllPendingEdges
            .Where(edge => replacementQns.Contains(edge.SourceQN) || replacementQns.Contains(edge.TargetQN))
            .ToList();
        var supportQns = selectedEdges
            .SelectMany(edge => new[] { edge.SourceQN, edge.TargetQN })
            .Where(qn => !replacementQns.Contains(qn))
            .ToHashSet(StringComparer.Ordinal);

        // A new file can introduce an entire folder chain. Pull in only the
        // structural ancestors needed to connect that new slice.
        var addedAncestor = true;
        while (addedAncestor)
        {
            addedAncestor = false;
            foreach (var edge in buffer.AllPendingEdges.Where(edge => edge.Type == EdgeType.CONTAINS_FOLDER))
            {
                if (!supportQns.Contains(edge.TargetQN))
                    continue;
                if (selectedEdges.Any(selected => selected == edge))
                    continue;

                selectedEdges.Add(edge);
                if (!replacementQns.Contains(edge.SourceQN))
                    addedAncestor |= supportQns.Add(edge.SourceQN);
            }
        }

        if (includeProjectStructure)
        {
            foreach (var node in buffer.AllNodes.Where(node =>
                         node.Label is NodeLabel.Repository or NodeLabel.DotnetProject))
                supportQns.Add(node.QualifiedName);
            selectedEdges.AddRange(buffer.AllPendingEdges.Where(edge => edge.Type == EdgeType.CONTAINS_PROJECT));
        }

        var supportingNodes = supportQns
            .Select(buffer.FindByQN)
            .Where(node => node is not null && string.IsNullOrWhiteSpace(node.FilePath))
            .Cast<GraphNode>();

        return (
            replacementNodes.Concat(supportingNodes)
                .DistinctBy(node => node.QualifiedName, StringComparer.Ordinal)
                .ToList(),
            selectedEdges.DistinctBy(
                    edge => $"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}",
                    StringComparer.Ordinal)
                .ToList());
    }

    private static string NormalizeAnalyzedDocumentPath(string rootPath, string documentPath) =>
        Path.GetFullPath(Path.IsPathRooted(documentPath)
            ? documentPath
            : Path.Combine(rootPath, documentPath));

    private static IReadOnlyList<ExtractionResult> PrepareRustSemanticResults(
        IReadOnlyList<ExtractionResult>? results)
    {
        if (results is null)
            throw new InvalidOperationException("The Rust analyzer returned a null result collection.");

        var prepared = new List<ExtractionResult>(results.Count);
        foreach (var result in results)
        {
            if (result is null)
                throw new InvalidOperationException("The Rust analyzer returned a null result.");

            var nodes = result.Nodes?.ToArray()
                ?? throw new InvalidOperationException("A Rust result contained a null node collection.");
            var edges = result.Edges?.ToArray()
                ?? throw new InvalidOperationException("A Rust result contained a null edge collection.");
            var calls = result.UnresolvedCalls?.ToArray()
                ?? throw new InvalidOperationException("A Rust result contained a null unresolved-call collection.");
            var imports = result.UnresolvedImports?.ToArray()
                ?? throw new InvalidOperationException("A Rust result contained a null unresolved-import collection.");

            if (nodes.Any(node => node is null ||
                                  string.IsNullOrWhiteSpace(node.Name) ||
                                  string.IsNullOrWhiteSpace(node.QualifiedName)))
            {
                throw new InvalidOperationException("A Rust result contained an invalid semantic node.");
            }

            if (edges.Any(edge => edge is null ||
                                  string.IsNullOrWhiteSpace(edge.SourceQN) ||
                                  string.IsNullOrWhiteSpace(edge.TargetQN)))
            {
                throw new InvalidOperationException("A Rust result contained an invalid semantic edge.");
            }

            if (calls.Any(call => call is null ||
                                  string.IsNullOrWhiteSpace(call.CallerQN) ||
                                  string.IsNullOrWhiteSpace(call.CalleeName)))
            {
                throw new InvalidOperationException("A Rust result contained an invalid unresolved call.");
            }

            if (imports.Any(import => import is null ||
                                      string.IsNullOrWhiteSpace(import.FileQN) ||
                                      string.IsNullOrWhiteSpace(import.ImportedNamespace)))
            {
                throw new InvalidOperationException("A Rust result contained an invalid unresolved import.");
            }

            if (result.Metadata is not null && string.IsNullOrWhiteSpace(result.Metadata.Language))
                throw new InvalidOperationException("A Rust result contained invalid project metadata.");

            prepared.Add(result with
            {
                Nodes = nodes,
                Edges = edges,
                UnresolvedCalls = calls,
                UnresolvedImports = imports
            });
        }

        if (prepared.Count == 0 || prepared.All(result => result.Nodes.Count == 0))
            throw new InvalidOperationException("The Rust analyzer returned no semantic definitions.");

        return prepared;
    }

    private static ProjectMetadata? SelectDominantMetadata(
        IEnumerable<ProjectMetadata?> metadata,
        IReadOnlyDictionary<string, LanguageStatAccumulator> languageStats)
    {
        var metadataByLanguage = metadata
            .Where(m => m is not null)
            .Cast<ProjectMetadata>()
            .GroupBy(m => m.Language, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        if (languageStats.Count == 0 && metadataByLanguage.Count == 0)
            return null;

        var primaryLanguage = languageStats.Values
            .OrderByDescending(stat => stat.LocNonBlank)
            .ThenByDescending(stat => stat.Files)
            .ThenByDescending(stat => GetLanguagePriority(stat.Language))
            .ThenBy(stat => stat.Language, StringComparer.OrdinalIgnoreCase)
            .Select(stat => stat.Language)
            .FirstOrDefault();

        if (primaryLanguage is not null &&
            metadataByLanguage.TryGetValue(primaryLanguage, out var candidates))
        {
            return candidates
                .OrderByDescending(candidate => candidate.DotnetSupport is not null)
                .ThenByDescending(candidate => string.IsNullOrWhiteSpace(candidate.Framework) ? 0 : 1)
                .First();
        }

        if (primaryLanguage is not null)
            return new ProjectMetadata(primaryLanguage, null);

        return metadataByLanguage
            .SelectMany(kvp => kvp.Value)
            .GroupBy(m => new { m.Language, m.Framework })
            .Select(group =>
            {
                var representative = group
                    .OrderByDescending(entry => entry.DotnetSupport is not null)
                    .First();
                return new { Metadata = representative, Count = group.Count() };
            })
            .OrderByDescending(kvp => kvp.Count)
            .ThenByDescending(kvp => GetLanguagePriority(kvp.Metadata.Language))
            .ThenBy(kvp => kvp.Metadata.Language, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => kvp.Metadata)
            .First();
    }

    private string? SerializeRepositoryProperties(
        ProjectMetadata metadata,
        IReadOnlyDictionary<string, LanguageStatAccumulator> languageStats)
    {
        var properties = DotnetSupportInspector.BuildRepositoryProperties(metadata.DotnetSupport) ??
                         new Dictionary<string, object>(StringComparer.Ordinal);

        if (languageStats.Count > 0)
        {
            var totalNonBlankLoc = languageStats.Values.Sum(stat => stat.LocNonBlank);
            properties["languageStats"] = languageStats
                .OrderByDescending(kvp => kvp.Value.LocNonBlank)
                .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new LanguageStatsSnapshot(
                        kvp.Value.Files,
                        kvp.Value.LocTotal,
                        kvp.Value.LocNonBlank,
                        kvp.Value.NodeCount,
                        totalNonBlankLoc == 0 ? 0 : Math.Round((double)kvp.Value.LocNonBlank / totalNonBlankLoc, 4)),
                    StringComparer.Ordinal);
            properties["primaryLanguageBasis"] = "locNonBlank";
        }

        return properties.Count == 0
            ? null
            : JsonSerializer.Serialize(
                properties,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
    }

    private Dictionary<string, LanguageStatAccumulator> ComputeLanguageStats(IReadOnlyList<string> files)
    {
        var result = new Dictionary<string, LanguageStatAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var language = GetLanguageForPath(file);
            if (language is null)
                continue;

            string content;
            try
            {
                content = _fileSystem.ReadAllText(file);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to count language LOC for {File}", file);
                continue;
            }

            if (content.Length > _options.MaxFileSizeKb * 1024)
                continue;

            var stat = GetOrAddLanguageStat(result, language);
            stat.Files++;
            stat.LocTotal += CountLines(content);
            stat.LocNonBlank += CountNonBlankLines(content);
        }

        return result;
    }

    private static void ApplyNodeCounts(
        IReadOnlyDictionary<string, LanguageStatAccumulator> languageStats,
        IEnumerable<GraphNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Label is NodeLabel.Repository or NodeLabel.Folder)
                continue;

            var language = GetLanguageForPath(node.FilePath);
            if (language is null)
                continue;

            if (languageStats.TryGetValue(language, out var stat))
                stat.NodeCount++;
        }
    }

    private static LanguageStatAccumulator GetOrAddLanguageStat(
        Dictionary<string, LanguageStatAccumulator> stats,
        string language)
    {
        if (!stats.TryGetValue(language, out var stat))
        {
            stat = new LanguageStatAccumulator(language);
            stats[language] = stat;
        }

        return stat;
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0)
            return 0;

        var count = 1;
        foreach (var ch in content)
        {
            if (ch == '\n')
                count++;
        }

        return content.EndsWith('\n') ? count - 1 : count;
    }

    private static int CountNonBlankLines(string content) =>
        content.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));

    private static string? GetLanguageForPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".ts" or ".tsx" => "TypeScript",
            ".js" or ".jsx" => "JavaScript",
            ".rs" => "Rust",
            ".py" or ".pyw" => "Python",
            ".go" => "Go",
            ".java" => "Java",
            ".rb" => "Ruby",
            ".php" => "PHP",
            ".sh" or ".bash" or ".zsh" => "Bash",
            ".c" or ".h" => "C",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" => "C++",
            ".sql" => "SQL",
            ".tf" or ".tfvars" => "Terraform",
            ".cfm" or ".cfc" => "ColdFusion",
            _ => null
        };

    private sealed class LanguageStatAccumulator(string language)
    {
        public string Language { get; } = language;
        public int Files { get; set; }
        public int LocTotal { get; set; }
        public int LocNonBlank { get; set; }
        public int NodeCount { get; set; }
    }

    private sealed record LanguageStatsSnapshot(
        int Files,
        int LocTotal,
        int LocNonBlank,
        int NodeCount,
        double LocShare);

    private sealed record PerFileExtractionResult(
        IReadOnlyList<ProjectMetadata> Metadata,
        IReadOnlyList<string> ProcessedFiles);

    private static int GetLanguagePriority(string language) =>
        language.ToLowerInvariant() switch
        {
            "c#" => 100,
            "typescript" => 95,
            "c++" => 90,
            "c" => 85,
            "go" => 80,
            "java" => 75,
            "rust" => 70,
            "python" => 60,
            "sql" => 50,
            "php" => 45,
            "ruby" => 40,
            "bash" => 10,
            _ => 0
        };
}
