using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Extractors.Rust;

public class RustProjectAnalyzer : IRustAnalyzer
{
    private static readonly TimeSpan DefaultScipGenerationTimeout = TimeSpan.FromMinutes(30);
    private const int DefaultStderrTailCharacters = 4096;
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly ILogger<RustProjectAnalyzer> _logger;
    private readonly TimeSpan _scipGenerationTimeout;
    private readonly int _stderrTailCharacters;

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
            optionsAccessor.Value.RustSemanticStderrTailCharacters)
    {
    }

    internal RustProjectAnalyzer(
        ILogger<RustProjectAnalyzer> logger,
        TimeSpan scipGenerationTimeout,
        int stderrTailCharacters = DefaultStderrTailCharacters)
    {
        if (scipGenerationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(scipGenerationTimeout));
        if (stderrTailCharacters is < 256 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(stderrTailCharacters));

        _logger = logger;
        _scipGenerationTimeout = scipGenerationTimeout;
        _stderrTailCharacters = stderrTailCharacters;
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
        var commandName = Path.GetFileName(fileName);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var result = new CommandResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask,
                stopwatch.Elapsed,
                GetTotalProcessorTime(process),
                GetPeakWorkingSetBytes(process));
            _logger.LogInformation(
                "Rust semantic command {Command} exited with {ExitCode} after {ElapsedMs}ms; CPU {CpuMs}ms; peak working set {PeakWorkingSetBytes} bytes; stderr tail: {StderrTail}",
                commandName,
                result.ExitCode,
                result.Elapsed.TotalMilliseconds,
                result.TotalProcessorTime.TotalMilliseconds,
                result.PeakWorkingSetBytes,
                result.Stderr);
            return result;
        }
        catch (OperationCanceledException)
        {
            await TerminateAndDrainAsync(
                process,
                processGroupId,
                stdoutTask,
                stderrTask);
            stopwatch.Stop();

            // Preserve caller cancellation as cancellation, rather than translating it
            // into a semantic-tool failure. Timeout remains a structured fatal failure.
            ct.ThrowIfCancellationRequested();
            var stderrTail = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            var cpuTime = GetTotalProcessorTime(process);
            var peakWorkingSetBytes = GetPeakWorkingSetBytes(process);
            _logger.LogError(
                "Rust semantic command {Command} timed out after {ElapsedMs}ms; CPU {CpuMs}ms; peak working set {PeakWorkingSetBytes} bytes; stderr tail: {StderrTail}",
                commandName,
                stopwatch.Elapsed.TotalMilliseconds,
                cpuTime.TotalMilliseconds,
                peakWorkingSetBytes,
                stderrTail);
            throw new RustSemanticIndexingException(
                "rust_semantic_command_timeout",
                $"Rust semantic command '{commandName}' exceeded " +
                $"the {_scipGenerationTimeout.TotalSeconds:0.###}-second timeout." +
                FormatDiagnosticSuffix(stderrTail));
        }
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
