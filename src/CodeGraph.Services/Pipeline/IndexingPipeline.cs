using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
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
    private readonly ExtractorExtensions _extensions;
    private readonly IndexingOptions _options;
    private readonly ILogger<IndexingPipeline> _logger;
    private readonly ISolutionAnalyzer? _solutionAnalyzer;
    private readonly INuGetReferenceExtractor? _nugetExtractor;
    private readonly ITypeScriptAnalyzer? _typeScriptAnalyzer;
    private readonly IRustAnalyzer? _rustAnalyzer;
    private readonly ICargoManifestExtractor? _cargoManifestExtractor;
    private readonly ISwiftPackageManifestExtractor? _swiftPackageManifestExtractor;
    private readonly IndexingRunDiagnostics? _diagnostics;
    private readonly IFileSystem _fileSystem;
    private readonly string[] _foundationalRepos;
    private readonly ExtractionVersionCatalog _extractionVersions;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryLocks =
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
        ICargoManifestExtractor? cargoManifestExtractor = null,
        IndexingRunDiagnostics? diagnostics = null,
        ISwiftPackageManifestExtractor? swiftPackageManifestExtractor = null)
    {
        _store = store;
        _extensions = new ExtractorExtensions(extractors);
        _options = optionsAccessor.Value;
        _fileSystem = fileSystem;
        _logger = logger;
        _solutionAnalyzer = solutionAnalyzer;
        _nugetExtractor = nugetExtractor;
        _typeScriptAnalyzer = typeScriptAnalyzer;
        _rustAnalyzer = rustAnalyzer;
        _cargoManifestExtractor = cargoManifestExtractor;
        _swiftPackageManifestExtractor = swiftPackageManifestExtractor;
        _diagnostics = diagnostics;
        _foundationalRepos = _options.FoundationalRepos ?? [];
        _extractionVersions = new ExtractionVersionCatalog(
            extractors, solutionAnalyzer, typeScriptAnalyzer, rustAnalyzer, swiftPackageManifestExtractor,
            DiscoveryExcludePatterns,
            FileSizeLimitsFingerprint,
            cargoManifestExtractor);
    }

    /// <summary>
    /// Digest of the current extraction version of every file type this pipeline indexes.
    /// Stored on sync state so routine syncs re-index unchanged commits after an extractor bump.
    /// </summary>
    public string ExtractionFingerprint => _extractionVersions.Fingerprint;

    /// <summary>Runs the per-run working-copy listing (sc-26199); a seam for tests.</summary>
    internal IGitProcessRunner GitRunner { get; init; } = GitProcessRunner.Instance;

    public async Task IndexRepositoryAsync(string repositoryName, string rootPath,
        FoundationalKnowledge? knowledge = null,
        IReadOnlyList<string>? changedFilesOnly = null,
        string? repoUrl = null,
        string? sourceGroup = null,
        string? repositoryToolingIdentity = null,
        bool replaceExistingGraph = false,
        SyncStateEntity? replacementSyncState = null,
        CancellationToken ct = default)
    {
        var repositoryLock = RepositoryLocks.GetOrAdd(repositoryName, _ => new SemaphoreSlim(1, 1));
        await repositoryLock.WaitAsync(ct);
        var phase = new StrongBox<string>("discovery");
        // Every discovery of this run shares one git listing per root.
        using var gitListings = GitListingScope.Begin(GitRunner, _logger);
        try
        {
            await IndexRepositoryCoreAsync(repositoryName, rootPath, knowledge, changedFilesOnly,
                repoUrl, sourceGroup, repositoryToolingIdentity, replaceExistingGraph, replacementSyncState, phase, ct);
        }
        catch (Exception ex) when (IndexingFailure.TagPhase(ex, phase.Value!))
        {
            throw; // unreachable: TagPhase only annotates
        }
        finally
        {
            repositoryLock.Release();
        }
    }

    private async Task IndexRepositoryCoreAsync(string repositoryName, string rootPath,
        FoundationalKnowledge? knowledge,
        IReadOnlyList<string>? changedFilesOnly,
        string? repoUrl,
        string? sourceGroup,
        string? repositoryToolingIdentity,
        bool replaceExistingGraph,
        SyncStateEntity? replacementSyncState,
        StrongBox<string> phase,
        CancellationToken ct)
    {
        var pipelineSw = Stopwatch.StartNew();
        _logger.LogInformation("Indexing {Repository} at {Path}", repositoryName, rootPath);

        var isFoundational = _foundationalRepos.Contains(repositoryName, StringComparer.OrdinalIgnoreCase);

        var repository = new RepositoryEntity
        {
            Name = repositoryName,
            LocalPath = rootPath,
            RepoUrl = repoUrl,
            SourceGroup = sourceGroup,
            IsFoundational = isFoundational
        };

        // Routine upserts need the repository row before node writes for the MariaDB FK.
        // Replacement creates/updates it inside the graph transaction instead. Only ensure
        // the row exists here: indexed_at and the index-result properties (languageStats,
        // rustSemanticOutcomes, rustSemanticFallbacks, ...) advance together after the graph
        // write succeeds, so a run that fails or is still running never looks completed.
        if (!replaceExistingGraph)
            await _store.EnsureRepositoryAsync(repository);

        var buffer = new GraphBuffer();
        var detectedMetadataCandidates = new List<ProjectMetadata>();
        var dotnetToolingTrusted = _options.IsDotnetToolingTrusted(repositoryToolingIdentity);
        var context = new ExtractorContext
        {
            RepositoryName = repositoryName,
            RootPath = rootPath,
            FoundationalKnowledge = knowledge,
            RepositoryToolingTrust = dotnetToolingTrusted
                ? RepositoryToolingTrust.Trusted
                : RepositoryToolingTrust.Untrusted
        };

        var existingHashes = replaceExistingGraph
            ? []
            : await _store.GetFileHashRecordsAsync(repositoryName);

        // Phase 1 — Discovery + Extraction
        var stepSw = Stopwatch.StartNew();
        var files = DiscoverFiles(rootPath, changedFilesOnly);
        var deletedFiles = GetDeletedFiles(repositoryName, rootPath, changedFilesOnly, files, existingHashes);
        var languageStats = ComputeLanguageStats(files);
        // Files over their size limit record their oversize version whichever component
        // processes them, so the stored version always matches what FilterByHash expects.
        var oversizeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesToProcess = FilterByHash(files, rootPath, existingHashes, buffer, oversizeFiles, out var outdatedVersionFiles);
        _logger.LogInformation("[Timing] Discovery + hashing: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);
        if (outdatedVersionFiles > 0)
        {
            _logger.LogInformation(
                "Re-extracting {Count} unchanged files of {Repository} whose extraction version is outdated",
                outdatedVersionFiles, repositoryName);
        }

        // If no files changed but the repository has no extraction nodes (e.g. extractor was
        // just added/fixed), force a full re-extraction by clearing hashes.
        if (filesToProcess.Count == 0 && files.Count > 0)
        {
            var existingNodes = await _store.GetAllNodesByRepositoryAsync(repositoryName);
            var hasExtractionNodes = existingNodes.Any(n =>
                n.Label is not ("Repository" or "DotnetProject" or "Folder" or "File"));
            if (!hasExtractionNodes)
            {
                _logger.LogInformation("No extraction nodes found for {Repository} despite {FileCount} files — forcing full re-extraction",
                    repositoryName, files.Count);
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
        // Same exclusions as source discovery (skip patterns, dependency directories, git rule).
        var csprojFiles = DiscoverPaths(rootPath, "**/*.csproj").ToArray();

        // Pass 1: Structural nodes (Repository, Folder, File)
        phase.Value = "extraction";
        stepSw.Restart();
        CreateStructuralNodes(repositoryName, rootPath, files, csprojFiles, buffer);
        _logger.LogInformation("[Timing] Structural nodes: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);

        // Pass 2: Extract code elements using specialized analyzers where available.
        // Track exact files completed by repository analyzers. A partial repository result must
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
                    "SECURITY-AUDIT: repository {Repository} with provider-resolved identity {RepositoryIdentity} is explicitly trusted by CodeGraph:IndexingOptions:TrustedDotnetRepositories; enabling repository-controlled restore and MSBuild solution analysis",
                    repositoryName,
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
                    "SECURITY-AUDIT: repository {Repository} with provider-resolved identity {RepositoryIdentity} is untrusted; restore and MSBuild solution analysis are disabled, using syntax-only C# extraction",
                    repositoryName,
                    repositoryToolingIdentity);
            }
        }

        // TypeScript/Angular — Node.js sidecar analysis. The sidecar discovers tsconfig files,
        // workspace packages and sources itself, so it is handed exactly the files discovery
        // admits (sc-26145): agent worktrees and dependency directories are never analyzed.
        IReadOnlyList<GraphNode>? typeScriptNodes = null;
        IReadOnlyList<ExtractionResult>? typeScriptResults = null;
        var typeScriptProcessedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_typeScriptAnalyzer is not null)
        {
            var typeScriptScope = DiscoverTypeScriptScope(rootPath);
            if (typeScriptScope.Any(IsTypeScriptPath))
            {
                // A synthetic path is the existing analyzer contract's default-config seam.
                var tsconfig = SelectTypeScriptConfig(typeScriptScope) ?? TypeScriptConfigFileName;
                _logger.LogInformation("Using TypeScript project analysis for {Tsconfig}", tsconfig);
                stepSw.Restart();
                try
                {
                    var results = (await _typeScriptAnalyzer.AnalyzeProjectAsync(
                            Path.Combine(rootPath, tsconfig), context, typeScriptScope, ct))
                        .Select(result => OwnTypeScriptResult(ClampToDiscovery(result, repositoryName, rootPath)))
                        .ToList();
                    if (results.Count == 0 || results.Any(result => !result.Succeeded || !result.Complete))
                        throw new InvalidOperationException("The TypeScript analyzer did not return complete successful repository output.");
                    _logger.LogInformation("[Timing] TypeScript repository analysis: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);
                    typeScriptResults = results;
                    typeScriptNodes = results.SelectMany(result => result.Nodes).ToList();
                    typeScriptProcessedFiles.UnionWith(results.SelectMany(result => result.ProcessedFiles).Select(NormalizeRelativePath));
                    var unprocessed = typeScriptScope.Where(IsTypeScriptPath)
                        .Where(path => !typeScriptProcessedFiles.Contains(path)).Take(5).ToList();
                    if (unprocessed.Count > 0)
                        throw new InvalidOperationException($"The TypeScript analyzer left admitted source files unprocessed: {string.Join(", ", unprocessed)}");
                    // Change-list runs may refresh derived facts for an unchanged entry file.
                    // Its structural File endpoint must be reinserted with that source slice.
                    var missingStructure = typeScriptProcessedFiles.Where(IsTypeScriptPath)
                        .Where(path => buffer.FindByQN($"{repositoryName}:{path}") is null)
                        .Select(path => Path.Combine(rootPath, path)).ToList();
                    if (missingStructure.Count > 0)
                        CreateStructuralNodes(repositoryName, rootPath, missingStructure, [], buffer);
                    AddMetadataCandidates(detectedMetadataCandidates, results);
                    MergeResults(results, buffer);
                    AddSuccessfullyProcessedFiles(results, changedRelativePaths, successfullyProcessedFiles);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "TypeScript project analysis incomplete for {Repository}; preserving the prior graph and source hashes",
                        repositoryName);
                    throw new InvalidOperationException($"TypeScript analysis incomplete for {repositoryName}; no graph or hash replacement was committed.", ex);
                }
            }
            else
            {
                typeScriptNodes = [];
                typeScriptResults = [];
            }
        }

        // Rust — Cargo/SCIP project analysis when rust-analyzer + SCIP data are available.
        var rustSemanticFallbacks = new List<RustSemanticFallback>();
        RustSemanticOutcomes? rustSemanticOutcomes = null;
        var rustSourceOwners = new List<RustSourceOwner>();
        if (_rustAnalyzer is not null)
        {
            // Every independent Cargo workspace (sc-26145: only the first one found was analyzed).
            var cargoManifests = CargoWorkspaceRoots.Find(_fileSystem, DiscoverCargoManifests(rootPath));

            foreach (var cargoManifest in cargoManifests)
            {
                _logger.LogInformation("Using Rust repository analysis for {Manifest}",
                    Path.GetRelativePath(rootPath, cargoManifest));
                stepSw.Restart();
                RustWorkspaceAnalysis analysis;
                try
                {
                    analysis = await _rustAnalyzer.AnalyzeWorkspaceAsync(cargoManifest, context, ct)
                        ?? throw new InvalidOperationException("The Rust analyzer returned no workspace analysis.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // No group could be analyzed (for example the tools are missing): the
                    // whole workspace falls back.
                    var failedAfterMs = stepSw.ElapsedMilliseconds;
                    var workspaceFallback = CreateRustSemanticFallback(rootPath, cargoManifest, cargoManifests, ex, crateGroup: null);
                    RecordRustSemanticFallback(workspaceFallback, ex);
                    rustSemanticOutcomes = RustSemanticOutcomes.Combine(rustSemanticOutcomes, RecordRustSemanticOutcomes(
                        repositoryName,
                        workspaceFallback.Manifest,
                        settings: null,
                        [new RustSemanticGroupOutcome(
                            0,
                            workspaceFallback.Crates,
                            RustSemanticGroupOutcome.Fallback,
                            workspaceFallback.FailureCode,
                            workspaceFallback.DetailCode,
                            failedAfterMs,
                            PeakRssMb: null,
                            Documents: null,
                            Nodes: null,
                            Edges: null)],
                        failedAfterMs));
                    continue;
                }

                // A failed semantic pass stays local to its crate group: nothing from a failed
                // group touches the graph buffer, so its .rs files fall through to the
                // registered tree-sitter Rust extractor and are marked low-confidence.
                var analysisMs = stepSw.ElapsedMilliseconds;
                var succeeded = new List<ExtractionResult>();
                var groupOutcomes = new List<RustSemanticGroupOutcome>(analysis.Groups.Count);
                foreach (var group in analysis.Groups)
                {
                    var failure = group.Failure;
                    IReadOnlyList<ExtractionResult>? prepared = null;
                    if (failure is null)
                    {
                        try
                        {
                            // Materialize and validate the complete semantic result before the
                            // shared graph buffer is touched, so a malformed/lazy result cannot
                            // leave a partial Rust graph behind before the tree-sitter fallback.
                            prepared = PrepareRustSemanticResults(group.Results);
                            succeeded.AddRange(prepared);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                    }

                    RustSemanticFallback? groupFallback = null;
                    if (failure is not null)
                    {
                        groupFallback = CreateRustSemanticFallback(rootPath, cargoManifest, cargoManifests, failure, group);
                        RecordRustSemanticFallback(groupFallback, failure);
                    }
                    else if (!group.IsWholeWorkspace)
                    {
                        rustSourceOwners.AddRange(group.SourceDirectories
                            .Select(directory => new RustSourceOwner(NormalizeRelativePath(directory), null)));
                    }

                    groupOutcomes.Add(CreateRustSemanticGroupOutcome(
                        rootPath,
                        cargoManifest,
                        cargoManifests,
                        group,
                        groupFallback,
                        prepared,
                        analysis.Groups.Count == 1 ? analysisMs : 0));
                }

                rustSemanticOutcomes = RustSemanticOutcomes.Combine(rustSemanticOutcomes, RecordRustSemanticOutcomes(
                    repositoryName,
                    NormalizeRelativePath(Path.GetRelativePath(rootPath, cargoManifest)),
                    analysis.Settings,
                    groupOutcomes,
                    analysisMs));
                _logger.LogInformation("[Timing] Rust repository analysis: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);
                if (succeeded.Count == 0)
                    continue;

                var linkedResults = RustScipCrossGroupLinker.Link(succeeded);
                MergeResults(linkedResults, buffer);
                AddMetadataCandidates(detectedMetadataCandidates, linkedResults);
                AddSuccessfullyProcessedFiles(
                    linkedResults,
                    changedRelativePaths,
                    successfullyProcessedFiles);
            }
        }

        void RecordRustSemanticFallback(RustSemanticFallback fallback, Exception failure)
        {
            rustSemanticFallbacks.Add(fallback);
            rustSourceOwners.AddRange(fallback.SourceDirectories
                .Select(directory => new RustSourceOwner(directory, fallback)));
            // This structured warning plus repositories.properties.rustSemanticFallbacks
            // is the durable record for runs without an indexer_runs row (scheduled,
            // CI and ProcessRepository consumer-driven indexing).
            _logger.LogWarning(failure,
                "Rust semantic indexing failed ({FailureCode}/{DetailCode}) for {Repository} {Manifest}; " +
                "falling back to low-confidence tree-sitter extraction for crates {Crates}",
                fallback.FailureCode,
                fallback.DetailCode,
                repositoryName,
                fallback.Manifest,
                fallback.Crates);
        }

        RustSemanticOutcomes RecordRustSemanticOutcomes(
            string repository,
            string manifest,
            RustSemanticSettingsSnapshot? settings,
            IReadOnlyList<RustSemanticGroupOutcome> groups,
            long totalDurationMs)
        {
            settings ??= new RustSemanticSettingsSnapshot(
                _options.RustSemanticCrateGroupSize,
                _options.RustSemanticGroupTimeoutSeconds,
                _options.RustSemanticWorkspaceTimeoutSeconds,
                _options.RustSemanticGroupMemoryLimitMb,
                _options.RustSemanticBuildScripts,
                _options.RustSemanticProcMacros);
            var outcomes = new RustSemanticOutcomes(
                manifest,
                groups.Sum(group => group.Crates.Count),
                groups.Count,
                settings.GroupSize,
                groups.Count(group => group.Outcome == RustSemanticGroupOutcome.Semantic),
                groups.Count(group => group.Outcome == RustSemanticGroupOutcome.Fallback),
                groups.Count(group => group.Outcome == RustSemanticGroupOutcome.Skipped),
                totalDurationMs,
                settings,
                groups);

            // Structured per-group record, also for runs without an indexer_runs row.
            foreach (var group in groups)
            {
                _logger.LogInformation(
                    "Rust semantic group outcome for {Repository} {Manifest}: group {Group} {Outcome} " +
                    "({FailureCode}/{DetailCode}) in {DurationMs}ms, peak RSS {PeakRssMb}MB, " +
                    "{Documents} documents, {Nodes} nodes, {Edges} edges; crates {Crates}",
                    repository,
                    manifest,
                    group.Index,
                    group.Outcome,
                    group.FailureCode,
                    group.DetailCode,
                    group.DurationMs,
                    group.PeakRssMb,
                    group.Documents,
                    group.Nodes,
                    group.Edges,
                    group.Crates);
            }

            _logger.LogInformation(
                "Rust semantic outcomes for {Repository} {Manifest}: {GroupCount} groups ({SemanticGroups} semantic, " +
                "{FallbackGroups} fallback, {SkippedGroups} skipped), {CrateCount} crates, {TotalDurationMs}ms " +
                "(group size {GroupSize}, group timeout {GroupTimeoutSeconds}s, workspace timeout " +
                "{WorkspaceTimeoutSeconds}s, group memory limit {GroupMemoryLimitMb}MB, build scripts " +
                "{BuildScripts}, proc-macros {ProcMacros})",
                repository,
                manifest,
                outcomes.GroupCount,
                outcomes.SemanticGroups,
                outcomes.FallbackGroups,
                outcomes.SkippedGroups,
                outcomes.CrateCount,
                outcomes.TotalDurationMs,
                settings.GroupSize,
                settings.GroupTimeoutSeconds,
                settings.WorkspaceTimeoutSeconds,
                settings.GroupMemoryLimitMb,
                settings.BuildScripts,
                settings.ProcMacros);
            return outcomes;
        }

        // Manifest files whose extracted package slice replaces the stored one on incremental runs.
        // Manifests are re-extracted on every run, but a manifest that is not a discovered source
        // file (or whose source extractor did not process it) never reaches successfullyProcessedFiles.
        var manifestReplacementPaths = new List<string>();
        // Manifest file names discovered this run, and the manifests found under them. A stored
        // package slice of such a manifest that was not found again is emptied below.
        var discoveredManifestNames = new HashSet<string>(StringComparer.Ordinal);
        var discoveredManifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Content hash records of the tracked manifests (Cargo.toml, Package.resolved,
        // project.pbxproj) whose slices this run writes. A manifest group whose manifests are all
        // unchanged (content, extractor version, set of paths) keeps its stored slice untouched.
        var manifestHashRecords = new Dictionary<string, FileHashRecord>(StringComparer.OrdinalIgnoreCase);

        // Cargo manifests — package definitions and dependency references for every crate/workspace member.
        if (_cargoManifestExtractor is not null)
        {
            var cargoManifestPaths = DiscoverCargoManifests(rootPath).ToArray();
            discoveredManifestNames.Add(CargoManifestFileName);
            discoveredManifestPaths.UnionWith(cargoManifestPaths.Select(path =>
                NormalizeRelativePath(Path.GetRelativePath(rootPath, path))));

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
                    var cargoRelativePaths = cargoManifestPaths
                        .Select(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)))
                        .ToList();
                    var cargoRecords = HashManifests(rootPath, cargoRelativePaths);
                    if (replaceExistingGraph || ManifestGroupChanged(
                            existingHashes, [CargoManifestFileName], cargoRecords, sourceManifestChanged: false))
                    {
                        manifestReplacementPaths.AddRange(cargoRelativePaths);
                        foreach (var (path, record) in cargoRecords)
                            manifestHashRecords[path] = record;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Cargo manifest extraction failed for {Repository}", repositoryName);
                }
            }
        }

        // Swift Package Manager / Xcode — package definitions, targets, declared dependencies,
        // resolved pins and Swift/Xcode vs Swift/SwiftPM classification.
        var swiftManifestsExtracted = false;
        var swiftManifestsPresent = false;
        if (_swiftPackageManifestExtractor is not null)
        {
            try
            {
                var swiftResult = ExtractSwiftPackageManifests(rootPath, context, out var swiftManifestPaths);
                discoveredManifestNames.Add(SwiftPackageManifestExtractor.PackageSwiftFileName);
                discoveredManifestNames.Add(SwiftPackageManifestExtractor.PackageResolvedFileName);
                discoveredManifestNames.Add(XcodeProjectReader.ProjectFileName);
                discoveredManifestPaths.UnionWith(swiftManifestPaths);
                swiftManifestsExtracted = true;
                swiftManifestsPresent = swiftResult is not null;
                if (swiftResult is not null)
                {
                    MergeResults([swiftResult], buffer);
                    if (swiftResult.Metadata is not null)
                        detectedMetadataCandidates.Add(swiftResult.Metadata);
                    // Package.resolved and project.pbxproj are not discovered source files: their
                    // slices are replaced whenever any Swift manifest changed (pins, declared
                    // dependencies and targets refer to each other). A changed Package.swift is
                    // replaced even when its Swift source extraction failed.
                    var swiftRecords = HashManifests(rootPath, swiftManifestPaths
                        .Where(path => ExtractionVersionCatalog.TrackedManifestNames.Contains(Path.GetFileName(path)))
                        .ToList());
                    var swiftGroupChanged = replaceExistingGraph || ManifestGroupChanged(
                        existingHashes,
                        [SwiftPackageManifestExtractor.PackageResolvedFileName, XcodeProjectReader.ProjectFileName],
                        swiftRecords,
                        sourceManifestChanged: changedRelativePaths.Concat(deletedFiles).Any(path =>
                            Path.GetFileName(path) == SwiftPackageManifestExtractor.PackageSwiftFileName));
                    manifestReplacementPaths.AddRange(swiftResult.ProcessedFiles.Where(path =>
                        changedRelativePaths.Contains(path) ||
                        (swiftGroupChanged && swiftRecords.ContainsKey(path))));
                    if (swiftGroupChanged)
                    {
                        foreach (var (path, record) in swiftRecords)
                            manifestHashRecords[path] = record;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Swift package manifest extraction failed for {Repository}", repositoryName);
            }
        }

        // A manifest that is gone (deleted, moved, or now excluded from discovery) leaves its package
        // slice behind: Package.resolved and Cargo.toml are not hash-tracked source files, so the
        // full scan never reports them deleted. Replacing their paths with nothing removes their
        // Package nodes and REFERENCES_PACKAGE edges; the post-index cross-repo linking of this
        // repository then rebuilds its links without them.
        if (!replaceExistingGraph && existingHashes.Count > 0 && discoveredManifestNames.Count > 0)
        {
            var removedManifests = await FindRemovedManifestPathsAsync(
                repositoryName, discoveredManifestNames, discoveredManifestPaths, existingHashes.Keys);
            manifestReplacementPaths.AddRange(removedManifests);
            swiftManifestsPresent |= removedManifests.Any(IsSwiftManifest);
        }
        swiftManifestsPresent |= deletedFiles.Any(IsSwiftManifest);

        // TypeScript slices no hash-tracked file covers: slices an earlier sidecar wrote under
        // excluded paths (agent worktrees, dependency directories) and workspace package modules
        // keyed by their directory. A wholesale TypeScript rewrite also sweeps edge-less stubs.
        var typeScriptReplacementPaths = new List<string>();
        IReadOnlyList<string> typeScriptOrphanCandidates = [];
        var typeScriptModulesChanged = false;
        if (!replaceExistingGraph && existingHashes.Count > 0 && _typeScriptAnalyzer is not null)
        {
            var excludedSlices = await FindExcludedTypeScriptSlicesAsync(repositoryName, rootPath, ct);
            typeScriptReplacementPaths.AddRange(excludedSlices);
            if (typeScriptNodes is not null)
            {
                var moduleSlices = await FindChangedTypeScriptModuleSlicesAsync(repositoryName, typeScriptNodes);
                typeScriptReplacementPaths.AddRange(moduleSlices);
                typeScriptModulesChanged = moduleSlices.Count > 0;
                var semanticSlices = await FindChangedTypeScriptSemanticSlicesAsync(repositoryName, typeScriptResults!, ct);
                typeScriptReplacementPaths.AddRange(semanticSlices);
                _logger.LogInformation("TypeScript semantic refresh of {Repository}: {Count} changed slice(s)", repositoryName, semanticSlices.Count);
            }
            var typeScriptVersionRefresh = changedRelativePaths.Any(path =>
                IsTypeScriptPath(path)
                && existingHashes.TryGetValue(path, out var record)
                && buffer.AllFileHashes.TryGetValue(path, out var hash)
                && record.ContentHash == hash);
            if (typeScriptReplacementPaths.Count > 0 || typeScriptVersionRefresh)
                typeScriptOrphanCandidates = await FindTypeScriptStubCandidatesAsync(repositoryName);
        }

        // DotnetProject nodes have no file slice: retire the ones no discovered .csproj yields.
        if (!replaceExistingGraph && existingHashes.Count > 0)
        {
            var (retired, packageCandidates) = await RetireStaleDotnetProjectsAsync(repositoryName, csprojFiles);
            if (retired)
            {
                typeScriptReplacementPaths.Add(StaleDotnetProjectSlice);
                typeScriptOrphanCandidates = typeScriptOrphanCandidates.Concat(packageCandidates).ToList();
            }
        }

        // Per-file extraction for everything not successfully handled by a specialized analyzer
        var remainingFiles = filesToProcess
            .Where(file => !typeScriptProcessedFiles.Contains(NormalizeRelativePath(Path.GetRelativePath(rootPath, file)))
                && !successfullyProcessedFiles.Contains(NormalizeRelativePath(Path.GetRelativePath(rootPath, file))))
            .ToList();

        if (remainingFiles.Count > 0)
        {
            stepSw.Restart();
            var perFileResult = await ExtractFilesAsync(
                remainingFiles, rootPath, context, buffer, replaceExistingGraph, rustSourceOwners, ct);
            _logger.LogInformation("[Timing] Per-file extraction ({FileCount} files): {ElapsedMs}ms", remainingFiles.Count, stepSw.ElapsedMilliseconds);
            detectedMetadataCandidates.AddRange(perFileResult.Metadata);
            successfullyProcessedFiles.UnionWith(perFileResult.ProcessedFiles);
            oversizeFiles.UnionWith(perFileResult.OversizeFiles);
        }

        // Extract NuGet package references from .csproj files (reuse cached discovery)
        if (_nugetExtractor is not null)
            ExtractNuGetReferences(repositoryName, csprojFiles, buffer, replaceExistingGraph);

        ApplyNodeCounts(languageStats, buffer.AllNodes);
        var detectedMetadata = SelectDominantMetadata(detectedMetadataCandidates, languageStats);
        // A fallback implies a Cargo workspace; keep metadata non-null so the repository
        // properties always carry the rustSemanticFallbacks record.
        if (detectedMetadata is null && (rustSemanticFallbacks.Count > 0 || rustSemanticOutcomes is not null))
            detectedMetadata = new ProjectMetadata("Rust", "Cargo");

        // Phase 2 — Resolution
        phase.Value = "resolution";
        stepSw.Restart();
        _logger.LogInformation("Pre-resolution: {Nodes} nodes, {PendingEdges} pending edges, {UnresolvedCalls} unresolved calls",
            buffer.AllNodes.Count, buffer.AllPendingEdges.Count, buffer.AllUnresolvedCalls.Count);
        ResolveImports(buffer);
        var includePersistedNodes = !replaceExistingGraph && existingHashes.Count > 0;
        var excludedPersistedFiles = successfullyProcessedFiles.Concat(deletedFiles)
            .Concat(typeScriptReplacementPaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Manifests are re-extracted every run, so persisted imports are re-bound whenever the
        // repository has (or just lost) Swift package manifests.
        var swiftImportRebinding = await ResolveSwiftImportsAsync(
            repositoryName,
            buffer,
            manifestsExtracted: swiftManifestsExtracted,
            incremental: includePersistedNodes,
            rebindPersisted: includePersistedNodes && swiftManifestsExtracted && swiftManifestsPresent,
            excludedPersistedFiles,
            ct);
        await ResolveCallsAsync(
            repositoryName, buffer, includePersistedNodes, excludedPersistedFiles, ct, swiftManifestsExtracted,
            swiftImportRebinding);
        var swiftRebinding = Combine(swiftImportRebinding, await ResolveSwiftTypeReferencesAsync(
            repositoryName, buffer, includePersistedNodes, excludedPersistedFiles, ct));
        CreateStubNodesForExternalTargets(repositoryName, buffer);
        _logger.LogInformation("[Timing] Resolution phase: {ElapsedMs}ms", stepSw.ElapsedMilliseconds);

        // Phase 3 — Flush
        phase.Value = "graph-write";
        stepSw.Restart();
        int edgeCount;
        if (replaceExistingGraph)
        {
            var (fileHashes, extractionVersions) = WithManifestRecords(
                buffer.AllFileHashes, ExtractionVersionsFor(buffer.AllFileHashes.Keys, oversizeFiles), manifestHashRecords);
            edgeCount = await _store.ReplaceRepositoryGraphAsync(
                repositoryName,
                buffer.AllNodes.ToList(),
                buffer.AllPendingEdges.ToList(),
                fileHashes,
                BuildRepositorySnapshot(
                    repository, detectedMetadata, languageStats, rustSemanticFallbacks, rustSemanticOutcomes),
                replacementSyncState,
                extractionVersions,
                ct);
            _logger.LogInformation(
                "[Timing] Atomic graph replacement ({NodeCount} nodes, {EdgeCount} edges): {ElapsedMs}ms",
                buffer.AllNodes.Count, edgeCount, stepSw.ElapsedMilliseconds);
        }
        else
        {
            var replacementPaths = successfullyProcessedFiles
                .Concat(deletedFiles)
                .Concat(manifestReplacementPaths)
                .Concat(typeScriptReplacementPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var (replacementNodes, replacementEdges) = BuildIncrementalSlice(
                buffer,
                replacementPaths,
                includeRepositoryStructure: existingHashes.Count == 0);
            AppendSwiftEdges(buffer, swiftRebinding, excludedPersistedFiles, replacementNodes, replacementEdges);
            if (typeScriptModulesChanged)
                AppendTypeScriptRootModules(buffer, typeScriptNodes!, replacementNodes, replacementEdges);
            // A change-list run can analyze unchanged callers outside its hash discovery list.
            // Replacing those derived slices must retain their successfully processed hash too.
            foreach (var path in typeScriptReplacementPaths.Where(path =>
                         IsTypeScriptPath(path) && typeScriptProcessedFiles.Contains(path)
                         && !buffer.AllFileHashes.ContainsKey(path)))
            {
                var fullPath = Path.Combine(rootPath, path);
                if (_fileSystem.FileExists(fullPath))
                    buffer.AddFileHash(path, Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(_fileSystem.ReadAllBytes(fullPath))));
            }
            var sourceHashes = buffer.AllFileHashes
                .Where(hash => successfullyProcessedFiles.Contains(hash.Key)
                    || (typeScriptProcessedFiles.Contains(hash.Key) && typeScriptReplacementPaths.Contains(hash.Key, StringComparer.OrdinalIgnoreCase)))
                .ToDictionary(hash => hash.Key, hash => hash.Value, StringComparer.OrdinalIgnoreCase);
            var (replacementHashes, replacementVersions) = WithManifestRecords(
                sourceHashes, ExtractionVersionsFor(sourceHashes.Keys, oversizeFiles), manifestHashRecords);

            edgeCount = await _store.ReplaceRepositoryFilesAsync(
                repositoryName,
                replacementPaths,
                replacementNodes,
                replacementEdges,
                replacementHashes,
                replacementVersions,
                WithOrphanCandidates(swiftRebinding.Retractions, typeScriptOrphanCandidates),
                ct);
            _logger.LogInformation(
                "[Timing] Atomic file-slice replacement ({FileCount} files, {NodeCount} nodes, {EdgeCount} edges): {ElapsedMs}ms",
                replacementPaths.Count, replacementNodes.Count, edgeCount, stepSw.ElapsedMilliseconds);
        }

        // Publish detected metadata only after the graph write succeeds.
        phase.Value = "repository-metadata";
        if (!replaceExistingGraph && detectedMetadata is not null)
        {
            await _store.UpsertRepositoryAsync(
                BuildRepositorySnapshot(
                    repository, detectedMetadata, languageStats, rustSemanticFallbacks, rustSemanticOutcomes));
        }

        // Report degradations only once the graph that reflects them has been committed.
        foreach (var fallback in rustSemanticFallbacks)
            _diagnostics?.RecordRustSemanticFallback(fallback);
        if (rustSemanticOutcomes is not null)
            _diagnostics?.RecordRustSemanticOutcomes(rustSemanticOutcomes);

        pipelineSw.Stop();
        _logger.LogInformation("Indexed {Repository}: {Nodes} nodes, {Edges} edges in {TotalMs}ms",
            repositoryName, buffer.AllNodes.Count, edgeCount, pipelineSw.ElapsedMilliseconds);
    }

    // ── File Discovery & Hashing ─────────────────────────────────────────

    /// <summary>
    /// Directories holding SwiftPM/Xcode build output or third-party checkouts
    /// (<c>.build/checkouts</c>, <c>.swiftpm</c>, <c>DerivedData/.../SourcePackages</c>). Their
    /// sources and manifests are dependencies, never the repository's own code, so every discovery
    /// walk (source files, Swift and Cargo manifests, overlays) skips them in addition to the
    /// configured <see cref="IndexingOptions.SkipPatterns"/>. Matched by exact (ordinal) name:
    /// these tools only ever create them with this spelling.
    /// </summary>
    internal static readonly IReadOnlyList<string> GeneratedDependencyDirectories =
        [".build", ".swiftpm", "DerivedData", "SourcePackages"];

    /// <summary>
    /// Dependency directories a package manager creates next to its manifest: CocoaPods installs
    /// <c>Pods/</c> beside the <c>Podfile</c>, Carthage checks out into <c>Carthage/</c> beside the
    /// <c>Cartfile</c> (or <c>Cartfile.resolved</c>), and Cargo writes its build output (generated
    /// build-script sources included) into <c>target/</c> beside the <c>Cargo.toml</c>. They are
    /// skipped only with that sibling manifest present and by exact name, so an ordinary
    /// <c>pkg/pods</c> package or a <c>target/</c> directory of the repository's own code stays indexed.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> ManagedDependencyDirectories =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Pods"] = ["Podfile"],
            ["Carthage"] = ["Cartfile", "Cartfile.resolved"],
            ["target"] = [CargoManifestFileName]
        };

    /// <summary>
    /// The legacy NuGet (packages.config) and Paket restore folder. NuGet restores each package into
    /// <c>packages/&lt;Id&gt;.&lt;Version&gt;/</c> holding <c>&lt;Id&gt;.&lt;Version&gt;.nupkg</c>,
    /// Paket into <c>packages/&lt;Id&gt;/</c>. A directory named exactly <c>packages</c> is skipped
    /// as a whole per <see cref="IsNuGetRestoreFolder"/> (restore markers, or .NET solution evidence
    /// unless it holds JS workspace packages). Independently, any directory directly under a
    /// <c>packages</c>/<c>Packages</c> folder (any case) that holds its own <c>&lt;name&gt;.nupkg</c>
    /// is skipped. A JS/TS monorepo <c>packages/*</c> or a SwiftPM <c>Packages/</c> directory
    /// without these .NET signals stays indexed.
    /// </summary>
    internal const string NuGetPackagesDirectory = "packages";

    private const string NuGetPackagesConfig = "packages.config";
    private const string NuGetRepositoriesConfig = "repositories.config";

    /// <summary>
    /// Every discovery exclusion as fingerprint text: the configured skip patterns and the
    /// generated, managed and NuGet dependency directory rules. Part of the extraction fingerprint,
    /// so a change re-runs indexing at an unchanged commit and the full scan adds or removes the
    /// files that entered or left the indexed universe.
    /// </summary>
    internal IEnumerable<string> DiscoveryExcludePatterns =>
        _options.SkipPatterns
            .Concat(GeneratedDependencyDirectories.Select(directory => $"dir:{directory}"))
            .Concat(ManagedDependencyDirectories
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"dir:{entry.Key}?sibling={string.Join(',', entry.Value)}"))
            .Append($"dir:{NuGetPackagesDirectory}?sibling={NuGetPackagesConfig},{string.Join(',', PaketManifests)}" +
                    $"&child={NuGetRepositoriesConfig}" +
                    $"&solution-sibling=*.sln,*.slnx,nuget.config,{string.Join(',', SolutionNuGetFiles)}" +
                    "&unless-children=package.json-without-nupkg" +
                    "&package-dir(ignore-case-parent)=<name>/<name>.nupkg")
            .Append(GitWorkingCopyRule);

    /// <summary>
    /// The git working-copy rule as fingerprint text (sc-26199): inside a git work tree only what
    /// <c>git ls-files --cached --others --exclude-standard</c> lists is discovered.
    /// </summary>
    internal const string GitWorkingCopyRule = "git:ls-files --stage --cached --others --exclude-standard;no-gitlinks;no-nested-repos";

    /// <summary>
    /// The effective file size limits as fingerprint text, so a limit change re-runs indexing at an
    /// unchanged commit and that run's full scan re-evaluates which files are oversize.
    /// </summary>
    private string FileSizeLimitsFingerprint =>
        string.Join(';', _options.MaxFileSizeKbByExtension
            .Select(entry => (Extension: entry.Key.ToLowerInvariant(), Limit: entry.Value))
            .OrderBy(entry => entry.Extension, StringComparer.Ordinal)
            .Select(entry => $"{entry.Extension}={entry.Limit}")
            .Prepend($"*={_options.MaxFileSizeKb}"));

    private const string CargoManifestFileName = "Cargo.toml";

    private static bool IsSwiftManifest(string path) =>
        Path.GetFileName(path) is SwiftPackageManifestExtractor.PackageSwiftFileName
            or SwiftPackageManifestExtractor.PackageResolvedFileName
            or XcodeProjectReader.ProjectFileName;

    /// <summary>The source file hashes and versions plus the manifest records of the slices this run writes.</summary>
    private static (Dictionary<string, string> Hashes, Dictionary<string, string> Versions) WithManifestRecords(
        IReadOnlyDictionary<string, string> sourceHashes,
        IReadOnlyDictionary<string, string> sourceVersions,
        IReadOnlyDictionary<string, FileHashRecord> manifestRecords)
    {
        var hashes = new Dictionary<string, string>(sourceHashes, StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, string>(sourceVersions, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, record) in manifestRecords)
        {
            hashes[path] = record.ContentHash;
            versions[path] = record.ExtractionVersion!;
        }

        return (hashes, versions);
    }

    /// <summary>Content hash and manifest version of each tracked manifest (repository-relative paths).</summary>
    private Dictionary<string, FileHashRecord> HashManifests(string rootPath, IReadOnlyList<string> relativePaths)
    {
        var records = new Dictionary<string, FileHashRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths)
        {
            if (_extractionVersions.ForManifest(path) is not { } version)
                continue;
            try
            {
                var bytes = _fileSystem.ReadAllBytes(Path.Combine(rootPath, path));
                records[path] = new FileHashRecord(Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(bytes)), version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No record: the next run sees the manifest as new and rewrites its group.
                _logger.LogDebug(ex, "Could not hash manifest {Manifest}", path);
            }
        }

        return records;
    }

    /// <summary>
    /// Whether a manifest group's stored slices are stale: a manifest is new, its content or
    /// manifest version changed, a stored manifest of the group is gone, or a manifest the group
    /// depends on (<paramref name="sourceManifestChanged"/>, e.g. a changed <c>Package.swift</c>)
    /// changed. Manifests of a group refer to each other (pins, path dependencies, targets), so the
    /// whole group is rewritten or none of it.
    /// </summary>
    private static bool ManifestGroupChanged(
        IReadOnlyDictionary<string, FileHashRecord> existingHashes,
        IReadOnlyCollection<string> manifestNames,
        IReadOnlyDictionary<string, FileHashRecord> current,
        bool sourceManifestChanged)
    {
        if (sourceManifestChanged)
            return true;

        foreach (var (path, record) in current)
        {
            if (!existingHashes.TryGetValue(path, out var stored)
                || stored.ContentHash != record.ContentHash
                || !string.Equals(stored.ExtractionVersion, record.ExtractionVersion, StringComparison.Ordinal))
                return true;
        }

        return existingHashes.Keys.Any(path =>
            manifestNames.Contains(Path.GetFileName(path)) && !current.ContainsKey(NormalizeRelativePath(path)));
    }

    /// <summary>
    /// Manifests (named one of <paramref name="manifestNames"/>) that own stored Package or Module
    /// nodes of <paramref name="repositoryName"/>, or a stored manifest hash record, but were not
    /// discovered this run.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindRemovedManifestPathsAsync(
        string repositoryName,
        IReadOnlySet<string> manifestNames,
        IReadOnlySet<string> discoveredPaths,
        IEnumerable<string> storedHashPaths)
    {
        var removed = (await _store.FindNodesByLabelAsync(repositoryName, NodeLabel.Package, int.MaxValue))
            .Concat(await _store.FindNodesByLabelAsync(repositoryName, NodeLabel.Module, int.MaxValue))
            .Select(node => node.FilePath)
            .OfType<string>()
            .Where(path => path.Length > 0)
            .Concat(storedHashPaths.Where(path =>
                ExtractionVersionCatalog.TrackedManifestNames.Contains(Path.GetFileName(path))))
            .Select(NormalizeRelativePath)
            .Where(path => manifestNames.Contains(Path.GetFileName(path)) && !discoveredPaths.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (removed.Count > 0)
        {
            _logger.LogInformation(
                "Removing the package slices of {Count} manifest(s) of {Repository} that are no longer present: {Manifests}",
                removed.Count, repositoryName, string.Join(", ", removed));
        }

        return removed;
    }

    /// <summary>
    /// A matcher for <paramref name="includePatterns"/> minus the configured skip patterns
    /// (case-insensitive globs). Dependency directories are excluded by <see cref="DiscoverPaths"/>
    /// and <see cref="IsDependencyPath"/>, not by this matcher.
    /// </summary>
    private Matcher CreateDiscoveryMatcher(params string[] includePatterns) =>
        CreateDiscoveryMatcher(_options.SkipPatterns, includePatterns);

    private static Matcher CreateDiscoveryMatcher(IEnumerable<string> skipPatterns, string[] includePatterns)
    {
        var matcher = new Matcher();
        matcher.AddIncludePatterns(includePatterns);
        foreach (var pattern in skipPatterns)
            matcher.AddExclude(pattern);
        return matcher;
    }

    /// <summary>
    /// Full paths of the files under <paramref name="rootPath"/> that match
    /// <paramref name="includePatterns"/> and no discovery exclusion. The walk never descends into
    /// a skipped dependency directory. Inside a git working copy only the files git lists are
    /// discovered (<see cref="GitWorkingTreeFiles"/>, sc-26199): gitignored paths and submodule
    /// working trees are left out, so a folder source sees what a clone of it holds plus its
    /// untracked, unignored files.
    /// </summary>
    private IEnumerable<string> DiscoverPaths(string rootPath, params string[] includePatterns) =>
        DiscoverPaths(rootPath, _fileSystem, _options.SkipPatterns, _logger, includePatterns);

    /// <summary>
    /// The discovery walk for callers outside the pipeline (vitals), with the given skip patterns
    /// (<see cref="IndexingOptions.SkipPatterns"/>) and the same dependency-directory rules, so
    /// they and graph discovery see the same file universe.
    /// </summary>
    internal static IEnumerable<string> DiscoverPaths(
        string rootPath, IFileSystem fileSystem, IEnumerable<string> skipPatterns, ILogger? logger,
        params string[] includePatterns)
    {
        var root = Path.GetFullPath(rootPath);
        var gitFiles = GitWorkingTreeFiles.TryList(root, logger);
        var tree = RepositoryTree.FromFileSystem(root, fileSystem, gitFiles);
        var directory = new DiscoveryDirectory(
            new DirectoryInfoWrapper(new DirectoryInfo(root)),
            fullPath =>
            {
                var relative = NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
                return (gitFiles is not null && !gitFiles.ContainsDirectory(relative))
                       || IsDependencyDirectory(relative, tree);
            });
        return CreateDiscoveryMatcher(skipPatterns, includePatterns).Execute(directory).Files
            .Where(match => gitFiles is null || gitFiles.ContainsFile(match.Path))
            .Select(match => Path.GetFullPath(Path.Combine(root, match.Path)));
    }

    /// <summary>
    /// True when the repository directory <paramref name="directoryPath"/> (repo-relative) is a
    /// skipped dependency directory; <paramref name="repositoryFileExists"/> answers for
    /// repository-relative file paths. Only files beside or directly inside the directory are
    /// probed, so the disk walk and the overlay's tree lookups decide identically.
    /// </summary>
    private static bool IsDependencyDirectory(string directoryPath, RepositoryTree tree)
    {
        var normalized = NormalizeRelativePath(directoryPath);
        if (normalized.Length == 0)
            return false;
        var separator = normalized.LastIndexOf('/');
        var name = separator < 0 ? normalized : normalized[(separator + 1)..];
        var parent = separator < 0 ? "" : normalized[..separator];
        string InParent(string file) => parent.Length == 0 ? file : $"{parent}/{file}";
        var parentName = parent[(parent.LastIndexOf('/') + 1)..];

        return GeneratedDependencyDirectories.Contains(name, StringComparer.Ordinal)
               || (ManagedDependencyDirectories.TryGetValue(name, out var manifests)
                   && manifests.Any(manifest => tree.FileExists(InParent(manifest))))
               || (name == NuGetPackagesDirectory && IsNuGetRestoreFolder(normalized, parent, tree))
               // One restored package directory: packages/<Id>.<Version>/<Id>.<Version>.nupkg. The
               // folder name is matched case-insensitively here (VS restores into Packages/ too);
               // the .nupkg probe keeps a SwiftPM Packages/<Name>/ indexed.
               || (string.Equals(parentName, NuGetPackagesDirectory, StringComparison.OrdinalIgnoreCase)
                   && tree.FileExists($"{normalized}/{name}.nupkg"));
    }

    /// <summary>Paket's lock/dependency files: Paket restores into packages/&lt;Id&gt;/.</summary>
    private static readonly string[] PaketManifests = ["paket.dependencies", "paket.lock"];

    /// <summary>NuGet configuration inside a sibling <c>.nuget/</c> directory (NuGet 2 solution restore).</summary>
    private static readonly string[] SolutionNuGetFiles =
        [".nuget/NuGet.Config", ".nuget/NuGet.config", ".nuget/nuget.config", ".nuget/packages.config"];

    /// <summary>
    /// True when the directory <paramref name="packagesDirectory"/> (named exactly <c>packages</c>,
    /// beside <paramref name="parent"/>'s other entries) is a NuGet/Paket restore folder as a whole.
    /// Precedence: a restore marker (a sibling <c>packages.config</c>, <c>paket.dependencies</c> or
    /// <c>paket.lock</c>, or <c>repositories.config</c> inside) always decides. Otherwise .NET
    /// solution evidence beside it (a <c>*.sln</c>/<c>*.slnx</c>, a <c>NuGet.Config</c>, or
    /// <c>.nuget/NuGet.Config</c>/<c>.nuget/packages.config</c>) makes it a restore folder unless its
    /// direct children look like JS workspace packages: some child holds a <c>package.json</c> and
    /// none holds a <c>.nupkg</c>.
    /// </summary>
    private static bool IsNuGetRestoreFolder(string packagesDirectory, string parent, RepositoryTree tree)
    {
        string InParent(string file) => parent.Length == 0 ? file : $"{parent}/{file}";
        if (tree.FileExists(InParent(NuGetPackagesConfig))
            || tree.FileExists($"{packagesDirectory}/{NuGetRepositoriesConfig}")
            || PaketManifests.Any(manifest => tree.FileExists(InParent(manifest))))
            return true;

        var siblings = tree.ListDirectory(parent);
        var solutionEvidence =
            siblings.Any(entry => entry.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                                  || entry.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                                  || entry.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
            || SolutionNuGetFiles.Any(file => tree.FileExists(InParent(file)));
        if (!solutionEvidence)
            return false;

        var hasWorkspacePackage = false;
        foreach (var child in tree.ListDirectory(packagesDirectory))
        {
            var entries = tree.ListDirectory($"{packagesDirectory}/{child}");
            if (entries.Any(entry => entry.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)))
                return true;
            hasWorkspacePackage |= entries.Contains("package.json");
        }

        return !hasWorkspacePackage;
    }

    /// <summary>
    /// True when a directory of <paramref name="relativePath"/> is a skipped dependency directory
    /// of <paramref name="tree"/>.
    /// </summary>
    private static bool IsDependencyPath(string relativePath, RepositoryTree tree)
    {
        var segments = NormalizeRelativePath(relativePath).Split('/');
        for (var index = 1; index < segments.Length; index++)
        {
            if (IsDependencyDirectory(string.Join('/', segments.Take(index)), tree))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every <c>Cargo.toml</c> discovery includes, minus Cargo build output under <c>target/</c>.
    /// </summary>
    private IReadOnlyList<string> DiscoverCargoManifests(string rootPath) =>
        DiscoverPaths(rootPath, $"**/{CargoManifestFileName}")
            .Where(path => Path.GetFileName(path) == CargoManifestFileName)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}target{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToList();

    private List<string> DiscoverFiles(string rootPath,
        IReadOnlyList<string>? changedFilesOnly)
    {
        if (changedFilesOnly != null)
            return changedFilesOnly
                .Select(f => Path.Combine(rootPath, f))
                .Where(_fileSystem.FileExists)
                .ToList();

        // A missing checkout must fail the run, never read as "every file was deleted".
        if (!_fileSystem.DirectoryExists(rootPath))
            throw new DirectoryNotFoundException($"Repository checkout not found at '{rootPath}'.");

        return DiscoverPaths(rootPath, "**/*")
            .Where(f => _extensions.IsSupportedExtension(Path.GetExtension(f)) || (_typeScriptAnalyzer is not null && IsTypeScriptPath(f)))
            .ToList();
    }

    /// <summary>
    /// Paths whose persisted graph slice must be removed. A change-list run reports listed paths
    /// that no longer exist. A full-scan incremental run reports every stored hash record that
    /// discovery no longer yields: the file was deleted, or it left the indexed universe (a skip
    /// pattern now excludes it, or no extractor supports its extension any more). A full scan
    /// fails closed: a missing checkout throws, and a scan that finds no files while hashes are
    /// stored infers no deletions rather than wiping the repository.
    /// </summary>
    private List<string> GetDeletedFiles(string repositoryName, string rootPath,
        IReadOnlyList<string>? changedFilesOnly, List<string> discoveredFiles,
        Dictionary<string, FileHashRecord> existingHashes)
    {
        if (changedFilesOnly is not null)
        {
            return changedFilesOnly
                .Where(path => !_fileSystem.FileExists(Path.Combine(rootPath, path)))
                .Select(NormalizeRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (existingHashes.Count == 0)
            return [];

        if (discoveredFiles.Count == 0)
        {
            _logger.LogWarning(
                "Full scan of {Repository} at {Path} discovered no files while {Count} files are indexed; " +
                "skipping deletion inference instead of removing the whole repository",
                repositoryName, rootPath, existingHashes.Count);
            return [];
        }

        var discovered = discoveredFiles
            .Select(file => NormalizeRelativePath(Path.GetRelativePath(rootPath, file)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Tracked manifests (Cargo.toml, Package.resolved, project.pbxproj) are not source files:
        // their records are reconciled with manifest discovery instead (FindRemovedManifestPathsAsync).
        var deleted = existingHashes.Keys
            .Select(NormalizeRelativePath)
            .Where(path => !discovered.Contains(path)
                           && !ExtractionVersionCatalog.TrackedManifestNames.Contains(Path.GetFileName(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (deleted.Count > 0)
        {
            _logger.LogInformation(
                "Full scan of {Repository} found {Count} indexed files no longer discovered; removing them",
                repositoryName, deleted.Count);
        }

        return deleted;
    }

    /// <summary>
    /// Files to extract: new files, files whose content changed, and unchanged files whose stored
    /// extraction version differs from the current one (a missing version counts as different).
    /// The current version of a file over its size limit is its <see cref="OversizeVersion"/>, so
    /// lowering a limit re-processes (and empties) an unchanged file that is now oversize, and
    /// raising it extracts a previously skipped one.
    /// </summary>
    private List<string> FilterByHash(List<string> files, string rootPath,
        Dictionary<string, FileHashRecord> existingHashes, GraphBuffer buffer, ISet<string> oversizeFiles,
        out int outdatedVersionFiles)
    {
        var changed = new List<string>();
        outdatedVersionFiles = 0;
        foreach (var file in files)
        {
            var relPath = NormalizeRelativePath(Path.GetRelativePath(rootPath, file));
            var bytes = _fileSystem.ReadAllBytes(file);
            var hash = Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(bytes));
            buffer.AddFileHash(relPath, hash);
            var oversize = IsOversize(file, relPath, bytes.Length);
            if (oversize)
                oversizeFiles.Add(relPath);

            if (!existingHashes.TryGetValue(relPath, out var existing) ||
                existing.ContentHash != hash)
            {
                changed.Add(file);
            }
            else if (!string.Equals(existing.ExtractionVersion,
                         oversize ? OversizeVersion(relPath) : _extractionVersions.ForPath(relPath),
                         StringComparison.Ordinal))
            {
                changed.Add(file);
                outdatedVersionFiles++;
            }
        }
        return changed;
    }

    private Dictionary<string, string> ExtractionVersionsFor(
        IEnumerable<string> relativePaths, IReadOnlySet<string> oversizeFiles) =>
        relativePaths.ToDictionary(
            path => path,
            path => oversizeFiles.Contains(path) ? OversizeVersion(path) : _extractionVersions.ForPath(path),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The version recorded for a file skipped as oversize: it names the limit, so the file is
    /// processed again once the limit (or its extractor) changes, but not while both stay. The
    /// limits are part of the extraction fingerprint, so a limit change triggers that run.
    /// </summary>
    private string OversizeVersion(string relativePath) =>
        $"{_extractionVersions.ForPath(relativePath)};oversize>{_options.MaxFileSizeKbFor(Path.GetExtension(relativePath))}KB";

    /// <summary>
    /// True when the file exceeds its size limit, by the same text-length test per-file extraction
    /// applies. Text is decoded only when the byte length exceeds the limit, since a file never
    /// holds more characters than bytes.
    /// </summary>
    private bool IsOversize(string file, string relativePath, int byteLength)
    {
        var limit = _options.MaxFileSizeKbFor(Path.GetExtension(relativePath)) * 1024;
        return byteLength > limit && _fileSystem.ReadAllText(file).Length > limit;
    }

    // ── Per-File Extraction ──────────────────────────────────────────────

    private async Task<PerFileExtractionResult> ExtractFilesAsync(List<string> files, string rootPath,
        ExtractorContext context, GraphBuffer buffer, bool failOnExtractionError,
        IReadOnlyList<RustSourceOwner> rustSourceOwners, CancellationToken ct)
    {
        var metadataSeen = new ConcurrentBag<ProjectMetadata>();
        var processedFiles = new ConcurrentBag<string>();
        var oversizeFiles = new ConcurrentBag<string>();
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
                var extractor = _extensions.ExtractorFor(ext);
                if (extractor is null) return;

                try
                {
                    var content = await _fileSystem.ReadAllTextAsync(filePath, ct2);

                    // A file over the size limit is not extracted, but it is processed: its File
                    // node says why it has no symbols and its hash is recorded, so an unchanged
                    // oversize file is not reported as changed again on the next run.
                    var limitKb = _options.MaxFileSizeKbFor(ext);
                    if (content.Length > limitKb * 1024)
                    {
                        var oversizePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath));
                        _logger.LogInformation(
                            "Skipping extraction of {File} in {Repository}: {SizeKb} KB exceeds the {LimitKb} KB limit",
                            oversizePath, context.RepositoryName, content.Length / 1024, limitKb);
                        if (buffer.FindByQN($"{context.RepositoryName}:{oversizePath}") is { Label: NodeLabel.File } fileNode)
                        {
                            fileNode.Properties["skipped_reason"] = "oversize";
                            fileNode.Properties["size_kb"] = content.Length / 1024;
                            fileNode.Properties["size_limit_kb"] = limitKb;
                        }
                        oversizeFiles.Add(oversizePath);
                        processedFiles.Add(oversizePath);
                        return;
                    }

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

                    var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath));
                    var fallback = FindRustSemanticFallback(rustSourceOwners, relativePath);
                    foreach (var node in result.Nodes)
                        buffer.AddNode(fallback is null ? node : MarkLowConfidence(node, fallback));
                    foreach (var edge in result.Edges)
                        buffer.AddEdge(edge);
                    foreach (var call in result.UnresolvedCalls)
                        buffer.AddUnresolvedCall(call);
                    foreach (var import in result.UnresolvedImports)
                        buffer.AddUnresolvedImport(import);

                    if (result.Metadata is not null)
                        metadataSeen.Add(result.Metadata);
                    processedFiles.Add(relativePath);
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

        return new PerFileExtractionResult(metadataSeen.ToList(), processedFiles.ToList(), oversizeFiles.ToList());
    }

    private RepositoryEntity BuildRepositorySnapshot(
        RepositoryEntity repository,
        ProjectMetadata? metadata,
        IReadOnlyDictionary<string, LanguageStatAccumulator> languageStats,
        IReadOnlyList<RustSemanticFallback> rustSemanticFallbacks,
        RustSemanticOutcomes? rustSemanticOutcomes)
    {
        repository.Language = metadata?.Language;
        repository.Framework = metadata?.Framework;
        repository.Properties = metadata is null
            ? null
            : SerializeRepositoryProperties(metadata, languageStats, rustSemanticFallbacks, rustSemanticOutcomes);
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
        bool includeRepositoryStructure)
    {
        var paths = replacementPaths
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replacementNodes = buffer.AllNodes
            .Where(node => includeRepositoryStructure ||
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

        if (includeRepositoryStructure)
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

    private RustSemanticFallback CreateRustSemanticFallback(
        string rootPath,
        string cargoManifest,
        IReadOnlyList<string> cargoRoots,
        Exception failure,
        RustCrateGroupAnalysis? crateGroup)
    {
        var manifest = NormalizeRelativePath(Path.GetRelativePath(rootPath, cargoManifest));
        var wholeWorkspace = crateGroup is null || crateGroup.IsWholeWorkspace;
        return new RustSemanticFallback(
            manifest,
            wholeWorkspace
                ? ReadCargoCrateNames(rootPath, cargoManifest, cargoRoots)
                : crateGroup!.Crates.Order(StringComparer.Ordinal).ToList(),
            RustSemanticFailureCategory.Classify(failure),
            (failure as RustSemanticIndexingException)?.FailureCode,
            failure.Message)
        {
            SourceDirectories = wholeWorkspace
                ? [NormalizeRelativePath(Path.GetDirectoryName(manifest) ?? "")]
                : crateGroup!.SourceDirectories.Select(NormalizeRelativePath).ToList()
        };
    }

    /// <summary>
    /// The crates a whole-workspace analysis of <paramref name="cargoManifest"/> covers: every
    /// discovered package manifest at or below its directory, except those owned by another
    /// analysis root nested below it. A standalone package nested in another package's directory
    /// (a vendored path dependency, a Tauri app) is analyzed through its own root, so it belongs
    /// to that root's group only (sc-26145: LensWorks counted such crates twice).
    /// </summary>
    private IReadOnlyList<string> ReadCargoCrateNames(
        string rootPath, string cargoManifest, IReadOnlyList<string> cargoRoots)
    {
        var workspaceRoot = Path.GetFullPath(Path.GetDirectoryName(cargoManifest) ?? rootPath);
        var nestedRoots = cargoRoots
            .Select(root => Path.GetFullPath(Path.GetDirectoryName(root) ?? rootPath))
            .Where(directory => IsStrictlyBelow(directory, workspaceRoot))
            .ToList();
        var crates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var manifestPath in DiscoverCargoManifests(rootPath)
                     .Where(path => IsAtOrBelow(Path.GetDirectoryName(path) ?? "", workspaceRoot))
                     .Where(path => !nestedRoots.Any(nested => IsAtOrBelow(Path.GetDirectoryName(path) ?? "", nested))))
        {
            try
            {
                var model = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(
                    _fileSystem.ReadAllText(manifestPath));
                if (model is not null &&
                    model.TryGetValue("package", out var package) &&
                    package is Tomlyn.Model.TomlTable packageTable &&
                    packageTable.TryGetValue("name", out var name) &&
                    name is string crateName &&
                    !string.IsNullOrWhiteSpace(crateName))
                {
                    crates.Add(crateName.Trim());
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read crate name from {Manifest}", manifestPath);
            }
        }

        return crates.ToList();
    }

    private static bool IsStrictlyBelow(string directory, string ancestor) =>
        directory.Length > ancestor.Length &&
        directory.StartsWith(ancestor, StringComparison.Ordinal) &&
        directory[ancestor.Length] == Path.DirectorySeparatorChar;

    private static bool IsAtOrBelow(string directory, string ancestor) =>
        string.Equals(directory, ancestor, StringComparison.Ordinal) || IsStrictlyBelow(directory, ancestor);

    /// <summary>
    /// The crate directory owning a <c>.rs</c> file is the longest matching source directory
    /// across every analyzed group, so a file in a nested crate belongs to that crate's group
    /// rather than to an enclosing (for example root) package's group.
    /// </summary>
    /// <param name="measuredWorkspaceMs">
    /// Duration to use when the analyzer did not time the group (a single whole-workspace group).
    /// </param>
    private RustSemanticGroupOutcome CreateRustSemanticGroupOutcome(
        string rootPath,
        string cargoManifest,
        IReadOnlyList<string> cargoRoots,
        RustCrateGroupAnalysis group,
        RustSemanticFallback? fallback,
        IReadOnlyList<ExtractionResult>? semanticResults,
        long measuredWorkspaceMs)
    {
        var crates = fallback?.Crates ?? (group.IsWholeWorkspace
            ? ReadCargoCrateNames(rootPath, cargoManifest, cargoRoots)
            : group.Crates.Order(StringComparer.Ordinal).ToList());
        var outcome = fallback is null
            ? RustSemanticGroupOutcome.Semantic
            : group.Skipped ? RustSemanticGroupOutcome.Skipped : RustSemanticGroupOutcome.Fallback;
        return new RustSemanticGroupOutcome(
            group.Index,
            crates,
            outcome,
            fallback?.FailureCode,
            fallback?.DetailCode,
            group.Duration is { } duration ? (long)duration.TotalMilliseconds : measuredWorkspaceMs,
            group.PeakRssBytes is { } peak ? (peak + BytesPerMegabyte - 1) / BytesPerMegabyte : null,
            semanticResults?.Sum(result => result.ProcessedFiles.Count),
            semanticResults?.Sum(result => result.Nodes.Count),
            semanticResults?.Sum(result => result.Edges.Count));
    }

    private const long BytesPerMegabyte = 1024L * 1024L;

    private static RustSemanticFallback? FindRustSemanticFallback(
        IReadOnlyList<RustSourceOwner> owners,
        string relativePath)
    {
        if (owners.Count == 0 ||
            !relativePath.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
            return null;

        return owners
            .Where(owner =>
                owner.Directory.Length == 0 ||
                relativePath.StartsWith(owner.Directory + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(owner => owner.Directory.Length)
            .ThenBy(owner => owner.Fallback is null ? 0 : 1)
            .FirstOrDefault()?
            .Fallback;
    }

    private sealed record RustSourceOwner(string Directory, RustSemanticFallback? Fallback);

    private static GraphNode MarkLowConfidence(GraphNode node, RustSemanticFallback fallback)
    {
        var properties = node.Properties is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(node.Properties);
        GraphNodeConfidence.MarkTreeSitterFallback(properties, fallback.FailureCode);
        return node with { Properties = properties };
    }

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
                throw new InvalidOperationException("A Rust result contained invalid repository metadata.");

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
        IReadOnlyDictionary<string, LanguageStatAccumulator> languageStats,
        IReadOnlyList<RustSemanticFallback> rustSemanticFallbacks,
        RustSemanticOutcomes? rustSemanticOutcomes)
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

        if (rustSemanticFallbacks.Count > 0)
            properties["rustSemanticFallbacks"] = rustSemanticFallbacks;
        if (rustSemanticOutcomes is not null)
            properties["rustSemanticOutcomes"] = rustSemanticOutcomes;

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

            if (content.Length > _options.MaxFileSizeKbFor(Path.GetExtension(file)) * 1024)
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
            ".swift" => "Swift",
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
        IReadOnlyList<string> ProcessedFiles,
        IReadOnlyList<string> OversizeFiles);

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
