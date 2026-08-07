using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.Rust;

public class RustProjectAnalyzer : IRustAnalyzer
{
    private static readonly TimeSpan DefaultScipGenerationTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly ILogger<RustProjectAnalyzer> _logger;
    private readonly TimeSpan _scipGenerationTimeout;

    public RustProjectAnalyzer(ILogger<RustProjectAnalyzer> logger)
        : this(logger, DefaultScipGenerationTimeout)
    {
    }

    internal RustProjectAnalyzer(
        ILogger<RustProjectAnalyzer> logger,
        TimeSpan scipGenerationTimeout)
    {
        _logger = logger;
        _scipGenerationTimeout = scipGenerationTimeout;
    }

    public async Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string cargoManifestPath, ExtractorContext context, CancellationToken ct = default)
    {
        var cargoRoot = Path.GetDirectoryName(cargoManifestPath) ?? context.RootPath;

        try
        {
            // A checked-in or leftover index.scip.json has no trustworthy binding to the
            // current checkout. Always generate from the selected Cargo project so stale
            // or wrong-project semantic data can never make an indexer run look healthy.
            var json = await GenerateScipJsonAsync(cargoRoot, ct);

            var result = ScipJsonImporter.Import(json, context);
            if (result.Nodes.Count == 0)
            {
                throw new RustSemanticIndexingException(
                    "rust_semantic_empty",
                    $"Rust semantic indexing produced no importable definitions for '{context.ProjectName}'.");
            }

            _logger.LogInformation(
                "Rust SCIP extraction complete: {Nodes} nodes, {Edges} edges",
                result.Nodes.Count,
                result.Edges.Count);

            return [result];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RustSemanticIndexingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RustSemanticIndexingException(
                "rust_semantic_import_failed",
                $"Rust semantic indexing failed for '{cargoManifestPath}'.",
                ex);
        }
    }

    private async Task<string> GenerateScipJsonAsync(string rootPath, CancellationToken ct)
    {
        var rustAnalyzer = FindExecutable("rust-analyzer");
        var scip = FindExecutable("scip");
        if (rustAnalyzer is null || scip is null)
        {
            throw new RustSemanticIndexingException(
                "rust_semantic_tools_unavailable",
                $"Rust semantic indexing tools are unavailable " +
                $"(rust-analyzer: {rustAnalyzer is not null}, scip: {scip is not null}).");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"codegraph-rust-scip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var scipPath = Path.Combine(tempDir, "index.scip");
            var generation = await RunCommandAsync(
                rustAnalyzer,
                ["scip", rootPath, "--output", scipPath],
                rootPath,
                captureStdout: false,
                ct);

            if (generation.ExitCode != 0)
            {
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
                scip,
                ["print", "--json", scipPath],
                tempDir,
                captureStdout: true,
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
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool captureStdout,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_scipGenerationTimeout);

        var processGroupFile = OperatingSystem.IsWindows()
            ? null
            : Path.Combine(Path.GetTempPath(), $"codegraph-process-group-{Guid.NewGuid():N}");

        using var process = new Process
        {
            StartInfo = CreateProcessStartInfo(
                fileName,
                arguments,
                workingDirectory,
                captureStdout,
                processGroupFile)
        };

        try
        {
            process.Start();
            var stdoutTask = captureStdout
                ? process.StandardOutput.ReadToEndAsync(timeout.Token)
                : Task.FromResult("");
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return new CommandResult(
                    process.ExitCode,
                    await stdoutTask,
                    await stderrTask);
            }
            catch (OperationCanceledException)
            {
                await TerminateAndDrainAsync(
                    process,
                    processGroupFile,
                    stdoutTask,
                    stderrTask);

                // Preserve caller cancellation as cancellation, rather than translating it
                // into a semantic-tool failure. Timeout remains a structured fatal failure.
                ct.ThrowIfCancellationRequested();
                throw new RustSemanticIndexingException(
                    "rust_semantic_command_timeout",
                    $"Rust semantic command '{Path.GetFileName(fileName)}' exceeded " +
                    $"the {_scipGenerationTimeout.TotalSeconds:0.###}-second timeout.");
            }
        }
        finally
        {
            if (processGroupFile is not null)
            {
                try { File.Delete(processGroupFile); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool captureStdout,
        string? processGroupFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = processGroupFile is null ? fileName : "/bin/sh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = captureStdout,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (processGroupFile is null)
        {
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            return startInfo;
        }

        // A non-interactive shell with job control starts the command in its own process
        // group. The supervisor remains alive long enough to kill that group even if the
        // command exits after spawning a descendant that inherited our redirected pipes.
        startInfo.Environment["CODEGRAPH_PROCESS_GROUP_FILE"] = processGroupFile;
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
        string? processGroupFile,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        if (processGroupFile is not null)
        {
            var processGroupId = await ReadProcessGroupIdAsync(processGroupFile);
            if (processGroupId is not null)
                KillUnixProcessGroup(processGroupId.Value);
        }

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

    private static async Task<int?> ReadProcessGroupIdAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        if (!File.Exists(path))
            return null;

        return int.TryParse(await File.ReadAllTextAsync(path), out var processGroupId)
            ? processGroupId
            : null;
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

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private const string UnixProcessSupervisorScript = """
        set -m
        "$@" &
        child=$!
        printf '%s' "$child" > "$CODEGRAPH_PROCESS_GROUP_FILE"
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
