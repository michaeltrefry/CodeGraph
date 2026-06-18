using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.Rust;

public class RustProjectAnalyzer : IRustAnalyzer
{
    private static readonly TimeSpan ScipGenerationTimeout = TimeSpan.FromMinutes(10);
    private readonly ILogger<RustProjectAnalyzer> _logger;

    public RustProjectAnalyzer(ILogger<RustProjectAnalyzer> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
        string cargoManifestPath, ExtractorContext context, CancellationToken ct = default)
    {
        var cargoRoot = Path.GetDirectoryName(cargoManifestPath) ?? context.RootPath;

        try
        {
            var json = await TryReadExistingScipJsonAsync(context.RootPath, cargoRoot, ct)
                ?? await TryGenerateScipJsonAsync(context.RootPath, ct);

            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogInformation(
                    "No SCIP JSON available for Rust project {Project}; falling back to per-file Rust extraction",
                    context.ProjectName);
                return [];
            }

            var result = ScipJsonImporter.Import(json, context);
            if (result.Nodes.Count == 0)
                return [];

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Rust SCIP extraction failed for {Manifest}; falling back to per-file Rust extraction",
                cargoManifestPath);
            return [];
        }
    }

    private static async Task<string?> TryReadExistingScipJsonAsync(
        string rootPath,
        string cargoRoot,
        CancellationToken ct)
    {
        var candidates = new[]
        {
            Path.Combine(rootPath, "index.scip.json"),
            Path.Combine(rootPath, ".codegraph", "index.scip.json"),
            Path.Combine(cargoRoot, "index.scip.json"),
            Path.Combine(cargoRoot, "target", "codegraph", "index.scip.json")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
                return await File.ReadAllTextAsync(candidate, ct);
        }

        return null;
    }

    private async Task<string?> TryGenerateScipJsonAsync(string rootPath, CancellationToken ct)
    {
        var rustAnalyzer = FindExecutable("rust-analyzer");
        var scip = FindExecutable("scip");
        if (rustAnalyzer is null || scip is null)
            return null;

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
                _logger.LogInformation(
                    "rust-analyzer scip exited with {ExitCode}; stderr: {Stderr}",
                    generation.ExitCode,
                    generation.Stderr);
                return null;
            }

            var printed = await RunCommandAsync(
                scip,
                ["print", "--json"],
                tempDir,
                captureStdout: true,
                ct);

            if (printed.ExitCode != 0)
            {
                _logger.LogInformation(
                    "scip print --json exited with {ExitCode}; stderr: {Stderr}",
                    printed.ExitCode,
                    printed.Stderr);
                return null;
            }

            return printed.Stdout;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool captureStdout,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ScipGenerationTimeout);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = captureStdout,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may already have exited */ }
            return new CommandResult(-1, "", "Command timed out.");
        }
    }

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
}
