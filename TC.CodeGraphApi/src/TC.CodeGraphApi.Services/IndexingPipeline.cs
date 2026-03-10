using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services;

public class IndexingPipeline
{
    private readonly IGraphStore _store;
    private readonly IEnumerable<ICodeExtractor> _extractors;
    private readonly IndexingOptions _options;
    private readonly ILogger<IndexingPipeline> _logger;
    private readonly ISolutionAnalyzer? _solutionAnalyzer;
    private readonly INuGetReferenceExtractor? _nugetExtractor;

    public IndexingPipeline(
        IGraphStore store,
        IEnumerable<ICodeExtractor> extractors,
        IOptions<IndexingOptions> options,
        ILogger<IndexingPipeline> logger,
        ISolutionAnalyzer? solutionAnalyzer = null,
        INuGetReferenceExtractor? nugetExtractor = null)
    {
        _store = store;
        _extractors = extractors;
        _options = options.Value;
        _logger = logger;
        _solutionAnalyzer = solutionAnalyzer;
        _nugetExtractor = nugetExtractor;
    }

    public async Task IndexProjectAsync(string projectName, string rootPath,
        FoundationalKnowledge? knowledge = null,
        IReadOnlyList<string>? changedFilesOnly = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Indexing {Project} at {Path}", projectName, rootPath);
        var buffer = new GraphBuffer();
        var context = new ExtractorContext
        {
            ProjectName = projectName,
            RootPath = rootPath,
            FoundationalKnowledge = knowledge
        };

        // Load existing file hashes for incremental indexing
        var existingHashes = await _store.GetFileHashesAsync(projectName);

        // Phase 1 — Discovery + Extraction
        var files = DiscoverFiles(rootPath, changedFilesOnly);
        var filesToProcess = FilterByHash(files, rootPath, existingHashes, buffer);

        _logger.LogInformation("Found {Total} files, {Changed} changed",
            files.Count, filesToProcess.Count);

        // Pass 1: Structural nodes (Project, Folder, File)
        CreateStructuralNodes(projectName, rootPath, files, buffer);

        // Pass 2: Extract code elements
        // Prefer solution-level analysis for C# when a .sln file is available
        var usedSolutionAnalysis = false;
        if (_solutionAnalyzer is not null)
        {
            var slnFiles = Directory.GetFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
            {
                _logger.LogInformation("Using solution-level Roslyn analysis for {Sln}",
                    Path.GetFileName(slnFiles[0]));

                var results = await _solutionAnalyzer.AnalyzeSolutionAsync(
                    slnFiles[0], context, ct);

                foreach (var result in results)
                {
                    foreach (var node in result.Nodes) buffer.AddNode(node);
                    foreach (var edge in result.Edges) buffer.AddEdge(edge);
                    foreach (var call in result.UnresolvedCalls) buffer.AddUnresolvedCall(call);
                    foreach (var import in result.UnresolvedImports) buffer.AddUnresolvedImport(import);
                }

                usedSolutionAnalysis = true;

                // Still run per-file extraction for non-.cs files
                var nonCsFiles = filesToProcess
                    .Where(f => Path.GetExtension(f) != ".cs")
                    .ToList();
                if (nonCsFiles.Count > 0)
                    await ExtractFilesAsync(nonCsFiles, rootPath, context, buffer, ct);
            }
        }

        if (!usedSolutionAnalysis)
        {
            // Fallback to per-file extraction
            await ExtractFilesAsync(filesToProcess, rootPath, context, buffer, ct);
        }

        // Extract NuGet package references from .csproj files
        if (_nugetExtractor is not null)
            ExtractNuGetReferences(projectName, rootPath, buffer);

        // Phase 2 — Resolution
        // Pass 3: Resolve imports
        ResolveImports(buffer);

        // Pass 4: Resolve calls
        ResolveCalls(buffer);

        // Pass 5: Resolve type references
        ResolveTypeReferences(buffer);

        // Phase 3 — Flush
        // Pass 6: Batch upsert all nodes
        var qnToId = await _store.UpsertNodeBatchAsync(buffer.AllNodes.ToList());

        // Pass 7: Resolve pending edges to IDs and batch insert
        var resolvedEdges = buffer.ResolveEdges(projectName, qnToId);
        await _store.InsertEdgeBatchAsync(resolvedEdges);

        // Pass 8: Store file hashes
        await _store.UpsertFileHashBatchAsync(projectName,
            buffer.AllFileHashes.ToDictionary(kv => kv.Key, kv => kv.Value));

        // Update project metadata
        await _store.UpsertProjectAsync(projectName, localPath: rootPath);

        _logger.LogInformation("Indexed {Project}: {Nodes} nodes, {Edges} edges",
            projectName, buffer.AllNodes.Count, resolvedEdges.Count);
    }

    private List<string> DiscoverFiles(string rootPath,
        IReadOnlyList<string>? changedFilesOnly)
    {
        if (changedFilesOnly != null)
            return changedFilesOnly
                .Select(f => Path.Combine(rootPath, f))
                .Where(File.Exists)
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

    private static string ComputeHash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = System.IO.Hashing.XxHash3.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    private async Task ExtractFilesAsync(List<string> files, string rootPath,
        ExtractorContext context, GraphBuffer buffer, CancellationToken ct)
    {
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
                    var content = await File.ReadAllTextAsync(filePath, ct2);

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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract {File}", filePath);
                    // Continue — don't let one file break the pipeline
                }
            });
    }

    private void CreateStructuralNodes(string projectName, string rootPath,
        List<string> files, GraphBuffer buffer)
    {
        // Project node
        buffer.AddNode(new GraphNode
        {
            Project = projectName,
            Label = NodeLabel.Project,
            Name = projectName,
            QualifiedName = projectName
        });

        // Folder and File nodes, with CONTAINS edges
        var folders = new HashSet<string>();
        foreach (var file in files)
        {
            var relPath = Path.GetRelativePath(rootPath, file);
            var relDir = Path.GetDirectoryName(relPath) ?? "";

            // File node
            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                Label = NodeLabel.File,
                Name = Path.GetFileName(file),
                QualifiedName = $"{projectName}:{relPath}",
                FilePath = relPath
            });

            // Folder nodes (walk up the directory tree)
            var dir = relDir;
            while (!string.IsNullOrEmpty(dir) && folders.Add(dir))
            {
                buffer.AddNode(new GraphNode
                {
                    Project = projectName,
                    Label = NodeLabel.Folder,
                    Name = Path.GetFileName(dir),
                    QualifiedName = $"{projectName}:{dir}"
                });

                var parentDir = Path.GetDirectoryName(dir) ?? "";
                var parentQN = string.IsNullOrEmpty(parentDir)
                    ? projectName
                    : $"{projectName}:{parentDir}";
                buffer.AddEdge(new PendingEdge(
                    parentQN,
                    $"{projectName}:{dir}",
                    EdgeType.CONTAINS_FOLDER));

                dir = parentDir;
            }

            // File containment edge
            var folderQN = string.IsNullOrEmpty(relDir)
                ? projectName
                : $"{projectName}:{relDir}";
            buffer.AddEdge(new PendingEdge(
                folderQN,
                $"{projectName}:{relPath}",
                EdgeType.CONTAINS_FILE));
        }
    }

    private void ExtractNuGetReferences(string projectName, string rootPath,
        GraphBuffer buffer)
    {
        var csprojFiles = Directory.GetFiles(rootPath, "*.csproj", SearchOption.AllDirectories);
        foreach (var csproj in csprojFiles)
        {
            try
            {
                var refs = _nugetExtractor!.ExtractFromProject(csproj);
                foreach (var (packageName, version) in refs)
                {
                    buffer.AddNode(new GraphNode
                    {
                        Project = projectName,
                        Label = NodeLabel.NuGetPackage,
                        Name = packageName,
                        QualifiedName = $"nuget:{packageName}",
                        Properties = new() { ["version"] = version }
                    });
                    buffer.AddEdge(new PendingEdge(
                        projectName,
                        $"nuget:{packageName}",
                        EdgeType.REFERENCES_PACKAGE,
                        new() { ["version"] = version }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract NuGet refs from {Csproj}", csproj);
            }
        }
    }

    /// <summary>
    /// Resolve import statements to namespace/type nodes.
    /// Phase 2 stub — full resolution happens when extractors populate UnresolvedImports.
    /// </summary>
    private void ResolveImports(GraphBuffer buffer)
    {
        foreach (var import in buffer.AllUnresolvedImports)
        {
            var target = buffer.FindByQN(import.ImportedNamespace);
            if (target != null)
            {
                buffer.AddEdge(new PendingEdge(
                    import.FileQN,
                    target.QualifiedName,
                    EdgeType.IMPORTS));
            }
        }
    }

    /// <summary>
    /// Resolve method calls to target method nodes.
    /// Phase 2 stub — full resolution happens when extractors populate UnresolvedCalls.
    /// </summary>
    private void ResolveCalls(GraphBuffer buffer)
    {
        foreach (var call in buffer.AllUnresolvedCalls)
        {
            // Try to find by qualified receiver type + method name
            if (call.ReceiverType != null)
            {
                var candidates = buffer.FindByName(call.CalleeName)
                    .Where(n => n.QualifiedName.StartsWith(call.ReceiverType))
                    .ToList();

                if (candidates.Count == 1)
                {
                    buffer.AddEdge(new PendingEdge(
                        call.CallerQN,
                        candidates[0].QualifiedName,
                        EdgeType.CALLS,
                        new Dictionary<string, object> { ["confidence"] = call.Confidence }));
                }
            }
        }
    }

    /// <summary>
    /// Resolve type references (USES_TYPE edges).
    /// Phase 2 stub — populated by Roslyn extractor in Phase 3.
    /// </summary>
    private void ResolveTypeReferences(GraphBuffer buffer)
    {
        // No-op until extractors produce type reference data
    }
}
