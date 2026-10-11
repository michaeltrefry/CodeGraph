using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Extractors.Rust;

/// <summary>
/// Generates rust-analyzer SCIP indexes for a Cargo workspace one crate group at a time.
/// <para>
/// The workspace is read once with <c>cargo metadata</c>. Each group is then described to
/// rust-analyzer as a <c>rust-project.json</c> in which only the group's crates are workspace
/// members, so <c>rust-analyzer scip</c> type-checks and emits documents for just those crates
/// while every other crate is a library loaded for name resolution. rust-analyzer never runs
/// build scripts for a rust-project.json workspace and only expands proc-macros whose dylib
/// the project file names, so both stay off unless <see cref="IndexingOptions.RustSemanticBuildScripts"/>
/// (and <see cref="IndexingOptions.RustSemanticProcMacros"/>) ask this analyzer to run
/// <c>cargo check</c> for the group and pass its outputs on.
/// </para>
/// </summary>
public class RustProjectAnalyzer : IRustAnalyzer
{
    private static readonly TimeSpan DefaultScipGenerationTimeout = TimeSpan.FromMinutes(10);
    private const int DefaultStderrTailCharacters = 4096;
    private const int SigkillExitCode = 137;
    private const string DefaultCgroupRootPath = "/sys/fs/cgroup";
    private const string DefaultProcRootPath = "/proc";
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MemorySampleInterval = TimeSpan.FromMilliseconds(250);
    private readonly ILogger<RustProjectAnalyzer> _logger;
    private readonly TimeSpan _scipGenerationTimeout;
    private readonly int _stderrTailCharacters;
    private readonly int _maxThreads;
    private readonly string _cgroupRootPath;
    private readonly string _procRootPath;
    private readonly RustCrateGroupSettings _groupSettings;

    public RustProjectAnalyzer(ILogger<RustProjectAnalyzer> logger)
        : this(logger, DefaultScipGenerationTimeout, DefaultStderrTailCharacters)
    {
    }

    public RustProjectAnalyzer(
        ILogger<RustProjectAnalyzer> logger,
        IOptions<IndexingOptions> optionsAccessor)
        : this(
            logger,
            TimeSpan.FromSeconds(optionsAccessor.Value.RustSemanticCommandTimeoutSeconds),
            optionsAccessor.Value.RustSemanticStderrTailCharacters,
            optionsAccessor.Value.RustSemanticMaxThreads,
            groupSettings: RustCrateGroupSettings.From(optionsAccessor.Value))
    {
    }

    internal RustProjectAnalyzer(
        ILogger<RustProjectAnalyzer> logger,
        TimeSpan scipGenerationTimeout,
        int stderrTailCharacters = DefaultStderrTailCharacters,
        int maxThreads = 2,
        string cgroupRootPath = DefaultCgroupRootPath,
        RustCrateGroupSettings? groupSettings = null,
        string procRootPath = DefaultProcRootPath)
    {
        if (scipGenerationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(scipGenerationTimeout));
        if (stderrTailCharacters is < 256 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(stderrTailCharacters));
        if (maxThreads is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maxThreads));
        if (string.IsNullOrWhiteSpace(cgroupRootPath))
            throw new ArgumentException("Cgroup root path is required.", nameof(cgroupRootPath));

        groupSettings ??= RustCrateGroupSettings.From(new IndexingOptions());
        if (groupSettings.GroupSize < 1)
            throw new ArgumentOutOfRangeException(nameof(groupSettings), "Crate group size must be positive.");
        if (groupSettings.GroupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(groupSettings), "Group timeout must be positive.");
        if (groupSettings.WorkspaceTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(groupSettings), "Workspace timeout must be positive.");
        if (groupSettings.MemoryLimitBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(groupSettings), "Group memory limit must not be negative.");
        if (groupSettings.ProcMacros && !groupSettings.BuildScripts)
            throw new ArgumentException("Proc-macro expansion requires build scripts.", nameof(groupSettings));

        _logger = logger;
        _scipGenerationTimeout = scipGenerationTimeout;
        _stderrTailCharacters = stderrTailCharacters;
        _maxThreads = maxThreads;
        _cgroupRootPath = cgroupRootPath;
        _procRootPath = procRootPath;
        _groupSettings = groupSettings;
    }

    /// <summary>
    /// Analyzes every crate group and returns their combined, cross-group-linked results, or
    /// rethrows the first group failure.
    /// </summary>
    public async Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string cargoManifestPath, ExtractorContext context, CancellationToken ct = default)
    {
        var analysis = await AnalyzeWorkspaceAsync(cargoManifestPath, context, ct);
        var failed = analysis.Groups.FirstOrDefault(group => group.Failure is not null);
        if (failed is not null)
            ExceptionDispatchInfo.Capture(failed.Failure!).Throw();

        return RustScipCrossGroupLinker.Link(
            analysis.Groups.SelectMany(group => group.Results ?? []).ToList());
    }

    public async Task<RustWorkspaceAnalysis> AnalyzeWorkspaceAsync(
        string cargoManifestPath, ExtractorContext context, CancellationToken ct = default)
    {
        var cargoRoot = Path.GetDirectoryName(cargoManifestPath) ?? context.RootPath;
        var tools = FindTools();
        var (workspace, sysroot) = await LoadWorkspaceAsync(tools, cargoManifestPath, cargoRoot, ct);
        var repositoryRoot = new RustPathMapper(context.RootPath, context.RootPath).RepositoryRoot;
        var groups = workspace.FormGroups(new RustPathMapper(repositoryRoot, repositoryRoot), _groupSettings.GroupSize);
        if (groups.Count == 0)
        {
            throw new RustSemanticIndexingException(
                "rust_semantic_empty",
                $"Cargo workspace '{cargoManifestPath}' has no members to analyze.");
        }

        _logger.LogInformation(
            "Rust semantic analysis of {Project}: {CrateCount} crates in {GroupCount} groups of up to {GroupSize} " +
            "(build scripts {BuildScripts}, proc-macros {ProcMacros})",
            context.ProjectName,
            workspace.WorkspaceMembers.Count,
            groups.Count,
            _groupSettings.GroupSize,
            _groupSettings.BuildScripts ? "on" : "off",
            _groupSettings.ProcMacros ? "on" : "off");

        // Groups run one after another; this deadline bounds all of them together.
        using var workspaceDeadline = new CancellationTokenSource(_groupSettings.WorkspaceTimeout);
        var outcomes = new List<RustCrateGroupAnalysis>(groups.Count);
        foreach (var group in groups)
        {
            if (workspaceDeadline.IsCancellationRequested)
            {
                var skipped = WorkspaceTimeoutExceeded(group);
                _logger.LogWarning(
                    skipped,
                    "Rust semantic group {Group} for {Project} skipped ({FailureCode}): crates {Crates}",
                    group.Index,
                    context.ProjectName,
                    skipped.FailureCode,
                    group.Crates);
                outcomes.Add(new RustCrateGroupAnalysis(group.Crates, group.SourceDirectories, null, skipped)
                {
                    Index = group.Index,
                    Skipped = true,
                    Duration = TimeSpan.Zero
                });
                continue;
            }

            outcomes.Add(await AnalyzeGroupAsync(
                tools, workspace, group, repositoryRoot, cargoManifestPath, cargoRoot, sysroot, context,
                workspaceDeadline.Token, ct));
        }

        return new RustWorkspaceAnalysis(outcomes) { Settings = _groupSettings.ToSnapshot() };
    }

    private async Task<RustCrateGroupAnalysis> AnalyzeGroupAsync(
        RustTools tools,
        RustCargoWorkspace workspace,
        RustCrateGroup group,
        string repositoryRoot,
        string cargoManifestPath,
        string cargoRoot,
        string sysroot,
        ExtractorContext context,
        CancellationToken workspaceDeadline,
        CancellationToken ct)
    {
        using var groupDeadline = CancellationTokenSource.CreateLinkedTokenSource(workspaceDeadline);
        groupDeadline.CancelAfter(_groupSettings.GroupTimeout);
        var budget = new CommandBudget(
            groupDeadline.Token,
            _groupSettings.GroupTimeout,
            _groupSettings.MemoryLimitBytes,
            workspaceDeadline,
            _groupSettings.WorkspaceTimeout,
            new PeakRssTracker());
        var stopwatch = Stopwatch.StartNew();
        var tempDir = Path.Combine(Path.GetTempPath(), $"codegraph-rust-scip-{Guid.NewGuid():N}");
        try
        {
            _logger.LogInformation(
                "Rust semantic group {Group} for {Project}: crates {Crates}",
                group.Index,
                context.ProjectName,
                group.Crates);

            var buildOutputs = _groupSettings.BuildScripts
                ? await RunBuildScriptsAsync(tools, workspace, group, cargoManifestPath, cargoRoot, budget, ct)
                : null;

            var analyzerRoot = Path.Combine(tempDir, "root");
            CreateAnalyzerView(repositoryRoot, analyzerRoot);
            var paths = new RustPathMapper(repositoryRoot, analyzerRoot);
            await File.WriteAllTextAsync(
                Path.Combine(analyzerRoot, "rust-project.json"),
                workspace.BuildProjectJson(group, paths, sysroot, buildOutputs),
                ct);

            var json = await GenerateScipJsonAsync(tools, analyzerRoot, tempDir, cargoRoot, budget, ct);
            var result = ScipJsonImporter.Import(json, context);
            if (result.Nodes.Count == 0)
            {
                throw new RustSemanticIndexingException(
                    "rust_semantic_empty",
                    $"Rust semantic indexing produced no importable definitions for crates " +
                    $"{string.Join(", ", group.Crates)} of '{context.ProjectName}'.");
            }

            _logger.LogInformation(
                "Rust SCIP extraction for group {Group} complete in {ElapsedMs}ms: {Nodes} nodes, {Edges} edges",
                group.Index,
                stopwatch.ElapsedMilliseconds,
                result.Nodes.Count,
                result.Edges.Count);
            return new RustCrateGroupAnalysis(group.Crates, group.SourceDirectories, [result], null)
            {
                Index = group.Index,
                Duration = stopwatch.Elapsed,
                PeakRssBytes = budget.PeakRss!.PeakBytes
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = ex as RustSemanticIndexingException ?? new RustSemanticIndexingException(
                "rust_semantic_import_failed",
                $"Rust semantic indexing failed for crates {string.Join(", ", group.Crates)} " +
                $"of '{cargoManifestPath}'.",
                ex);
            _logger.LogWarning(
                failure,
                "Rust semantic group {Group} for {Project} failed ({FailureCode}) after {ElapsedMs}ms: crates {Crates}",
                group.Index,
                context.ProjectName,
                failure.FailureCode,
                stopwatch.ElapsedMilliseconds,
                group.Crates);
            return new RustCrateGroupAnalysis(group.Crates, group.SourceDirectories, null, failure)
            {
                Index = group.Index,
                Duration = stopwatch.Elapsed,
                PeakRssBytes = budget.PeakRss!.PeakBytes
            };
        }
        finally
        {
            DeleteAnalyzerView(tempDir);
        }
    }

    private RustTools FindTools()
    {
        var rustAnalyzer = FindExecutable("rust-analyzer");
        var scip = FindExecutable("scip");
        var cargo = FindExecutable("cargo");
        var rustc = FindExecutable("rustc");
        if (rustAnalyzer is null || scip is null || cargo is null || rustc is null)
        {
            throw new RustSemanticIndexingException(
                "rust_semantic_tools_unavailable",
                $"Rust semantic indexing tools are unavailable " +
                $"(rust-analyzer: {rustAnalyzer is not null}, scip: {scip is not null}, " +
                $"cargo: {cargo is not null}, rustc: {rustc is not null}).");
        }

        return new RustTools(rustAnalyzer, scip, cargo, rustc);
    }

    private async Task<(RustCargoWorkspace Workspace, string Sysroot)> LoadWorkspaceAsync(
        RustTools tools,
        string cargoManifestPath,
        string cargoRoot,
        CancellationToken ct)
    {
        var metadata = await RunCommandAsync(
            tools.Cargo,
            ["metadata", "--format-version", "1", "--manifest-path", cargoManifestPath],
            cargoRoot,
            captureStdout: true,
            CommandBudget.None,
            ct);
        if (metadata.ExitCode != 0)
        {
            throw new RustSemanticIndexingException(
                "cargo_metadata_failed",
                $"cargo metadata exited with {metadata.ExitCode}: {metadata.Stderr}");
        }

        RustCargoWorkspace workspace;
        try
        {
            workspace = RustCargoWorkspace.Parse(metadata.Stdout);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new RustSemanticIndexingException(
                "cargo_metadata_failed",
                $"cargo metadata produced unreadable output for '{cargoManifestPath}'.",
                ex);
        }

        // rustup resolves the repository's toolchain file from this working directory, the
        // same way cargo does for the metadata above.
        var sysroot = await RunCommandAsync(
            tools.Rustc,
            ["--print", "sysroot"],
            cargoRoot,
            captureStdout: true,
            CommandBudget.None,
            ct);
        var sysrootPath = sysroot.Stdout.Trim();
        if (sysroot.ExitCode != 0 || sysrootPath.Length == 0)
        {
            throw new RustSemanticIndexingException(
                "rust_sysroot_unavailable",
                $"rustc --print sysroot exited with {sysroot.ExitCode}: {sysroot.Stderr}");
        }

        return (workspace, sysrootPath);
    }

    private async Task<RustBuildOutputs> RunBuildScriptsAsync(
        RustTools tools,
        RustCargoWorkspace workspace,
        RustCrateGroup group,
        string cargoManifestPath,
        string cargoRoot,
        CommandBudget budget,
        CancellationToken ct)
    {
        var arguments = new List<string>
        {
            "check", "--quiet", "--message-format=json", "--all-targets",
            "--manifest-path", cargoManifestPath
        };
        foreach (var packageId in group.PackageIds)
        {
            arguments.Add("--package");
            var package = workspace.Packages[packageId];
            arguments.Add($"{package.Name}@{package.Version}");
        }

        var check = await RunCommandAsync(tools.Cargo, arguments, cargoRoot, captureStdout: true, budget, ct);
        var outputs = new RustBuildOutputs(_groupSettings.ProcMacros);
        outputs.AddCargoMessages(check.Stdout);
        if (check.ExitCode != 0)
        {
            // Like rust-analyzer's own build-script pass, compile errors leave the outputs
            // that were produced usable; the index is still generated for the group.
            _logger.LogWarning(
                "cargo check for Rust semantic group {Group} exited with {ExitCode}; continuing with partial build-script outputs. {Stderr}",
                group.Index,
                check.ExitCode,
                check.Stderr);
        }

        return outputs;
    }

    /// <summary>
    /// Creates <paramref name="analyzerRoot"/> with one symlink per top-level checkout entry
    /// (except a checked-in rust-project.json, which would shadow the generated one).
    /// </summary>
    private static void CreateAnalyzerView(string repositoryRoot, string analyzerRoot)
    {
        Directory.CreateDirectory(analyzerRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(repositoryRoot))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, "rust-project.json", StringComparison.Ordinal))
                continue;

            var link = Path.Combine(analyzerRoot, name);
            if (Directory.Exists(entry))
                Directory.CreateSymbolicLink(link, entry);
            else
                File.CreateSymbolicLink(link, entry);
        }
    }

    private static void DeleteAnalyzerView(string tempDir)
    {
        try
        {
            var analyzerRoot = Path.Combine(tempDir, "root");
            if (Directory.Exists(analyzerRoot))
            {
                // Remove the links themselves first so cleanup can never reach into the checkout.
                foreach (var entry in new DirectoryInfo(analyzerRoot).EnumerateFileSystemInfos())
                {
                    if (entry.LinkTarget is not null)
                        entry.Delete();
                }
            }

            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private async Task<string> GenerateScipJsonAsync(
        RustTools tools,
        string analyzerRoot,
        string tempDir,
        string cargoRoot,
        CommandBudget budget,
        CancellationToken ct)
    {
        var scipPath = Path.Combine(tempDir, "index.scip");
        var generation = await RunCommandAsync(
            tools.RustAnalyzer,
            [
                "scip",
                analyzerRoot,
                "--output",
                scipPath,
                // CodeGraph owns the selected repository's source graph. Keeping
                // dependency documents makes rust-analyzer traverse tens of thousands
                // of vendored definitions while local references still retain their
                // external SCIP symbols without those documents.
                "--exclude-vendored-libraries",
                "--num-threads",
                _maxThreads.ToString(CultureInfo.InvariantCulture)
            ],
            cargoRoot,
            captureStdout: false,
            budget,
            ct);

        if (generation.ExitCode != 0)
        {
            if (generation.ExitCode == SigkillExitCode)
            {
                throw new RustSemanticIndexingException(
                    "rust_semantic_resource_exhausted",
                    "rust-analyzer scip was killed with exit 137 (SIGKILL), consistent with " +
                    "container memory exhaustion. " +
                    $"Process peak working set={generation.PeakWorkingSetBytes} bytes. " +
                    ReadCgroupMemoryDiagnostics(_cgroupRootPath) +
                    FormatDiagnosticSuffix(generation.Stderr));
            }

            throw new RustSemanticIndexingException(
                "rust_analyzer_scip_failed",
                $"rust-analyzer scip exited with {generation.ExitCode}: {generation.Stderr}");
        }

        if (!File.Exists(scipPath))
        {
            throw new RustSemanticIndexingException(
                "rust_analyzer_scip_missing_output",
                "rust-analyzer scip reported success without creating an index.");
        }

        var printed = await RunCommandAsync(
            tools.Scip,
            ["print", "--json", scipPath],
            tempDir,
            captureStdout: true,
            budget,
            ct);

        if (printed.ExitCode != 0)
        {
            throw new RustSemanticIndexingException(
                "scip_print_failed",
                $"scip print --json exited with {printed.ExitCode}: {printed.Stderr}");
        }

        if (string.IsNullOrWhiteSpace(printed.Stdout))
        {
            throw new RustSemanticIndexingException(
                "scip_print_empty",
                "scip print --json produced no output.");
        }

        return printed.Stdout;
    }

    private async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool captureStdout,
        CommandBudget budget,
        CancellationToken ct)
    {
        var commandName = Path.GetFileName(fileName);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.GroupDeadline);
        timeout.CancelAfter(_scipGenerationTimeout);

        var useSetsid = !OperatingSystem.IsWindows() && File.Exists("/usr/bin/setsid");

        using var process = new Process
        {
            StartInfo = CreateProcessStartInfo(
                fileName,
                arguments,
                workingDirectory,
                captureStdout,
                useSetsid)
        };

        _logger.LogInformation(
            "Starting Rust semantic command {Command} in {WorkingDirectory} with timeout {TimeoutSeconds}s",
            commandName,
            workingDirectory,
            _scipGenerationTimeout.TotalSeconds);
        process.Start();
        var processGroupId = useSetsid ? process.Id : (int?)null;
        var stdoutTask = captureStdout
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult("");
        var stderrTask = ReadBoundedTailAsync(process.StandardError, _stderrTailCharacters);
        using var watchdogStop = new CancellationTokenSource();
        var watchdog = StartMemoryWatchdog(
            process, processGroupId, budget.MemoryLimitBytes, budget.PeakRss is not null, watchdogStop.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var memory = await StopWatchdogAsync(watchdog, watchdogStop);
            budget.PeakRss?.Observe(memory);
            var result = new CommandResult(
                process.ExitCode,
                await stdoutTask.WaitAsync(timeout.Token),
                await stderrTask.WaitAsync(timeout.Token),
                stopwatch.Elapsed,
                GetTotalProcessorTime(process),
                GetPeakWorkingSetBytes(process));
            _logger.LogInformation(
                "Rust semantic command {Command} exited with {ExitCode} after {ElapsedMs}ms; CPU {CpuMs}ms; peak working set {PeakWorkingSetBytes} bytes; peak process-group RSS {PeakGroupRssBytes} bytes; stderr tail: {StderrTail}",
                commandName,
                result.ExitCode,
                result.Elapsed.TotalMilliseconds,
                result.TotalProcessorTime.TotalMilliseconds,
                result.PeakWorkingSetBytes,
                memory.PeakBytes,
                result.Stderr);
            if (memory.Exceeded)
                throw MemoryLimitExceeded(commandName, budget.MemoryLimitBytes, memory.PeakBytes, result.Stderr);
            return result;
        }
        catch (OperationCanceledException)
        {
            // Process metrics become unavailable after the process is reaped. Capture
            // them before terminating the process tree so timeout diagnostics do not
            // misleadingly report zero CPU and memory for a resource-heavy command.
            var cpuTime = GetTotalProcessorTime(process);
            var peakWorkingSetBytes = GetPeakWorkingSetBytes(process);
            await TerminateAndDrainAsync(
                process,
                processGroupId,
                stdoutTask,
                stderrTask);
            var memory = await StopWatchdogAsync(watchdog, watchdogStop);
            budget.PeakRss?.Observe(memory);
            stopwatch.Stop();

            // Preserve caller cancellation as cancellation, rather than translating it
            // into a semantic-tool failure. Timeout remains a structured fatal failure.
            ct.ThrowIfCancellationRequested();
            var stderrTail = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            _logger.LogError(
                "Rust semantic command {Command} timed out after {ElapsedMs}ms; CPU {CpuMs}ms; peak working set {PeakWorkingSetBytes} bytes; stderr tail: {StderrTail}",
                commandName,
                stopwatch.Elapsed.TotalMilliseconds,
                cpuTime.TotalMilliseconds,
                peakWorkingSetBytes,
                stderrTail);
            if (memory.Exceeded)
                throw MemoryLimitExceeded(commandName, budget.MemoryLimitBytes, memory.PeakBytes, stderrTail);
            if (budget.WorkspaceDeadline.IsCancellationRequested)
            {
                throw new RustSemanticIndexingException(
                    "rust_semantic_workspace_timeout",
                    $"Rust semantic analysis exceeded the " +
                    $"{budget.WorkspaceTimeout.TotalSeconds:0.###}-second workspace timeout during '{commandName}'." +
                    FormatDiagnosticSuffix(stderrTail));
            }

            if (budget.GroupDeadline.IsCancellationRequested)
            {
                throw new RustSemanticIndexingException(
                    "rust_semantic_group_timeout",
                    $"Rust semantic crate group exceeded the " +
                    $"{budget.GroupTimeout.TotalSeconds:0.###}-second group timeout during '{commandName}'." +
                    FormatDiagnosticSuffix(stderrTail));
            }

            throw new RustSemanticIndexingException(
                "rust_semantic_command_timeout",
                $"Rust semantic command '{commandName}' exceeded " +
                $"the {_scipGenerationTimeout.TotalSeconds:0.###}-second timeout." +
                FormatDiagnosticSuffix(stderrTail));
        }
    }

    private RustSemanticIndexingException WorkspaceTimeoutExceeded(RustCrateGroup group) =>
        new(
            "rust_semantic_workspace_timeout",
            $"Rust semantic analysis exceeded the {_groupSettings.WorkspaceTimeout.TotalSeconds:0.###}-second " +
            $"workspace timeout before crates {string.Join(", ", group.Crates)} were analyzed.");

    private RustSemanticIndexingException MemoryLimitExceeded(
        string commandName,
        long limitBytes,
        long peakBytes,
        string stderrTail) =>
        new(
            "rust_semantic_memory_limit_exceeded",
            $"Rust semantic command '{commandName}' was killed after its process group's resident " +
            $"memory reached {peakBytes} bytes, above the {limitBytes}-byte crate-group limit. " +
            ReadCgroupMemoryDiagnostics(_cgroupRootPath) +
            FormatDiagnosticSuffix(stderrTail));

    /// <summary>
    /// Enforces the crate-group memory cap on Linux: samples the summed RSS of the command's
    /// process group (setsid makes it the command's own) from /proc and kills the whole group
    /// once it exceeds the cap. Without a process group or /proc nothing is sampled or enforced.
    /// Crate-group commands are sampled for their peak even without a cap (limit 0).
    /// </summary>
    private Task<MemorySample> StartMemoryWatchdog(
        Process process,
        int? processGroupId,
        long limitBytes,
        bool sampleWithoutLimit,
        CancellationToken stop)
    {
        if (limitBytes <= 0 && !sampleWithoutLimit)
            return Task.FromResult(new MemorySample(0, false, false));
        if (processGroupId is null || !Directory.Exists(_procRootPath))
        {
            _logger.LogDebug(
                "Rust semantic memory limit is not enforced on this platform (no setsid process group or {ProcRoot}).",
                _procRootPath);
            return Task.FromResult(new MemorySample(0, false, false));
        }

        return Task.Run(async () =>
        {
            long peak = 0;
            while (!stop.IsCancellationRequested)
            {
                var rss = ReadProcessGroupRssBytes(_procRootPath, processGroupId.Value);
                peak = Math.Max(peak, rss);
                if (limitBytes > 0 && rss > limitBytes)
                {
                    KillUnixProcessGroup(processGroupId.Value);
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* already exited */ }
                    return new MemorySample(peak, true, true);
                }

                try { await Task.Delay(MemorySampleInterval, stop); }
                catch (OperationCanceledException) { break; }
            }

            return new MemorySample(peak, false, true);
        }, CancellationToken.None);
    }

    private static async Task<MemorySample> StopWatchdogAsync(
        Task<MemorySample> watchdog,
        CancellationTokenSource stop)
    {
        await stop.CancelAsync();
        return await watchdog;
    }

    /// <summary>
    /// Sums the resident set size of every process in <paramref name="processGroupId"/> from
    /// <c>/proc/&lt;pid&gt;/stat</c> (field 5 = process group, field 24 = RSS pages).
    /// </summary>
    internal static long ReadProcessGroupRssBytes(string procRootPath, int processGroupId)
    {
        long pages = 0;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(procRootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                continue;

            try
            {
                var stat = File.ReadAllText(Path.Combine(entry, "stat"));
                // The command name (field 2) is parenthesized and may contain spaces.
                var commandEnd = stat.LastIndexOf(')');
                if (commandEnd < 0 || commandEnd + 2 > stat.Length)
                    continue;

                var fields = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 21 &&
                    int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group) &&
                    group == processGroupId &&
                    long.TryParse(fields[21], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rssPages))
                {
                    pages += rssPages;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Processes exit while being sampled.
            }
        }

        return pages * Environment.SystemPageSize;
    }

    private static async Task<string> ReadBoundedTailAsync(StreamReader reader, int maxCharacters)
    {
        var tail = new StringBuilder(maxCharacters);
        var buffer = new char[Math.Min(4096, maxCharacters)];
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0)
                break;

            tail.Append(buffer, 0, count);
            if (tail.Length > maxCharacters)
                tail.Remove(0, tail.Length - maxCharacters);
        }

        return tail.ToString().Trim();
    }

    private static string FormatDiagnosticSuffix(string stderrTail)
        => string.IsNullOrWhiteSpace(stderrTail) ? "" : $" Diagnostic tail: {stderrTail}";

    internal static string ReadCgroupMemoryDiagnostics(string cgroupRootPath)
    {
        var diagnostics = new List<string>();
        AddCgroupValue(diagnostics, cgroupRootPath, "memory.current");
        AddCgroupValue(diagnostics, cgroupRootPath, "memory.peak");
        AddCgroupValue(diagnostics, cgroupRootPath, "memory.max");

        var eventsPath = Path.Combine(cgroupRootPath, "memory.events");
        try
        {
            if (File.Exists(eventsPath))
            {
                var interestingEvents = new HashSet<string>(StringComparer.Ordinal)
                {
                    "high", "max", "oom", "oom_kill", "oom_group_kill"
                };
                foreach (var line in File.ReadLines(eventsPath))
                {
                    var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && interestingEvents.Contains(parts[0]))
                        diagnostics.Add($"{parts[0]}={parts[1].Trim()}");
                }
            }
        }
        catch (IOException)
        {
            // A cgroup can disappear while its container is shutting down.
        }
        catch (UnauthorizedAccessException)
        {
            // Some runtimes expose the cgroup hierarchy without readable metrics.
        }

        return diagnostics.Count == 0
            ? "Cgroup memory metrics unavailable."
            : $"Cgroup memory: {string.Join(", ", diagnostics)}.";
    }

    private static void AddCgroupValue(
        ICollection<string> diagnostics,
        string cgroupRootPath,
        string fileName)
    {
        try
        {
            var path = Path.Combine(cgroupRootPath, fileName);
            if (!File.Exists(path))
                return;

            var value = File.ReadAllText(path).Trim();
            if (value.Length > 0)
                diagnostics.Add($"{fileName}={value}");
        }
        catch (IOException)
        {
            // Best-effort diagnostics must not hide the original analyzer failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort diagnostics must not hide the original analyzer failure.
        }
    }

    private static TimeSpan GetTotalProcessorTime(Process process)
    {
        try { return process.TotalProcessorTime; }
        catch (Exception) { return TimeSpan.Zero; }
    }

    private static long GetPeakWorkingSetBytes(Process process)
    {
        try { return process.PeakWorkingSet64; }
        catch (Exception) { return 0; }
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool captureStdout,
        bool useSetsid)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = useSetsid
                ? "/usr/bin/setsid"
                : OperatingSystem.IsWindows() ? fileName : "/bin/sh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = captureStdout,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            return startInfo;
        }

        if (useSetsid)
        {
            // A freshly spawned process cannot already lead its inherited process group,
            // so setsid execs the tool in a new session whose process-group id is the
            // Process.Id retained by the caller even after the tool itself exits.
            startInfo.ArgumentList.Add(fileName);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            return startInfo;
        }

        // macOS does not ship setsid. Its /bin/sh supports monitored background jobs,
        // so retain a supervisor there for local development; production Linux images
        // use the explicit setsid boundary above.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(UnixProcessSupervisorScript);
        startInfo.ArgumentList.Add("codegraph-process-supervisor");
        startInfo.ArgumentList.Add(fileName);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task TerminateAndDrainAsync(
        Process process,
        int? processGroupId,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        if (processGroupId is not null)
            KillUnixProcessGroup(processGroupId.Value);

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* process exited between checks */ }
        }

        using var cleanup = new CancellationTokenSource(ProcessCleanupTimeout);
        try { await process.WaitForExitAsync(cleanup.Token); }
        catch (InvalidOperationException) { /* process was already reaped */ }
        catch (OperationCanceledException) { /* cleanup remains bounded */ }

        try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(cleanup.Token); }
        catch (OperationCanceledException) { /* reads use the linked command deadline */ }
    }

    private static void KillUnixProcessGroup(int processGroupId)
    {
        const int sigkill = 9;
        _ = kill(-processGroupId, sigkill);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private sealed record RustTools(string RustAnalyzer, string Scip, string Cargo, string Rustc);

    /// <param name="Sampled">True when /proc was sampled for the command's process group.</param>
    private sealed record MemorySample(long PeakBytes, bool Exceeded, bool Sampled);

    /// <summary>Highest process-group RSS sampled across one crate group's commands.</summary>
    private sealed class PeakRssTracker
    {
        /// <summary>Null until a command of the group was sampled from /proc.</summary>
        public long? PeakBytes { get; private set; }

        public void Observe(MemorySample sample)
        {
            if (sample.Sampled)
                PeakBytes = Math.Max(PeakBytes ?? 0, sample.PeakBytes);
        }
    }

    /// <param name="PeakRss">Collects the group's peak RSS; null for workspace-level commands.</param>
    private sealed record CommandBudget(
        CancellationToken GroupDeadline,
        TimeSpan GroupTimeout,
        long MemoryLimitBytes,
        CancellationToken WorkspaceDeadline,
        TimeSpan WorkspaceTimeout,
        PeakRssTracker? PeakRss = null)
    {
        public static CommandBudget None { get; } = new(
            CancellationToken.None, Timeout.InfiniteTimeSpan, 0, CancellationToken.None, Timeout.InfiniteTimeSpan);
    }

    private sealed record CommandResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        TimeSpan Elapsed,
        TimeSpan TotalProcessorTime,
        long PeakWorkingSetBytes);

    private const string UnixProcessSupervisorScript = """
        set -m
        "$@" &
        child=$!
        cleanup() {
          trap - EXIT HUP INT TERM
          kill -KILL -"$child" 2>/dev/null || true
          wait "$child" 2>/dev/null || true
        }
        trap cleanup EXIT HUP INT TERM
        wait "$child"
        status=$?
        cleanup
        exit "$status"
        """;
}

/// <summary>Crate-group settings for <see cref="RustProjectAnalyzer"/>.</summary>
internal sealed record RustCrateGroupSettings(
    int GroupSize,
    TimeSpan GroupTimeout,
    long MemoryLimitBytes,
    bool BuildScripts,
    bool ProcMacros,
    TimeSpan WorkspaceTimeout)
{
    public static RustCrateGroupSettings From(IndexingOptions options) => new(
        options.RustSemanticCrateGroupSize,
        TimeSpan.FromSeconds(options.RustSemanticGroupTimeoutSeconds),
        options.RustSemanticGroupMemoryLimitMb * 1024L * 1024L,
        options.RustSemanticBuildScripts,
        options.RustSemanticProcMacros,
        TimeSpan.FromSeconds(options.RustSemanticWorkspaceTimeoutSeconds));

    public RustSemanticSettingsSnapshot ToSnapshot() => new(
        GroupSize,
        (int)GroupTimeout.TotalSeconds,
        (int)WorkspaceTimeout.TotalSeconds,
        (int)(MemoryLimitBytes / (1024L * 1024L)),
        BuildScripts,
        ProcMacros);
}
