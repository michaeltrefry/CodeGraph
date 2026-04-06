using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;
using TC.CodeGraphApi.Services.Analyzers;

namespace TC.CodeGraphApi.Extractors.CSharp;

public class SolutionAnalyzer : ISolutionAnalyzer
{
    private static int _msBuildRegistered;
    private readonly ILogger<SolutionAnalyzer> _logger;
    private readonly LintResultCache _lintCache;

    public SolutionAnalyzer(ILogger<SolutionAnalyzer> logger, LintResultCache lintCache)
    {
        _logger = logger;
        _lintCache = lintCache;
        EnsureMSBuildRegistered();
    }

    private static void EnsureMSBuildRegistered()
    {
        if (Interlocked.CompareExchange(ref _msBuildRegistered, 1, 0) == 0)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }


    public async Task<IReadOnlyList<ExtractionResult>> AnalyzeSolutionAsync(
        string solutionPath, ExtractorContext context, CancellationToken ct)
    {
        _logger.LogInformation("Opening solution {Solution}", solutionPath);

        await RestoreNuGetPackagesAsync(solutionPath, ct);

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
            _logger.LogWarning("Workspace warning: {Message}", e.Diagnostic.Message);

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        var results = new ConcurrentBag<ExtractionResult>();

        _logger.LogInformation("Analyzing {Count} projects in solution", solution.Projects.Count());

        var diagnosticCounts = new ConcurrentDictionary<string, (int Errors, int Warnings)>(StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(solution.Projects, ct, async (project, ct2) =>
        {
            var compilation = await project.GetCompilationAsync(ct2);
            if (compilation is null)
            {
                _logger.LogWarning("Could not compile project {Project}", project.Name);
                return;
            }

            // Capture Roslyn diagnostics per file for trust scoring
            foreach (var diag in compilation.GetDiagnostics(ct2))
            {
                if (diag.Location.SourceTree?.FilePath is not { Length: > 0 } filePath) continue;

                var relPath = Path.GetRelativePath(context.RootPath, filePath).Replace('\\', '/');
                diagnosticCounts.AddOrUpdate(relPath,
                    diag.Severity == DiagnosticSeverity.Error ? (1, 0) : (0, 1),
                    (_, prev) => diag.Severity == DiagnosticSeverity.Error
                        ? (prev.Errors + 1, prev.Warnings)
                        : (prev.Errors, prev.Warnings + 1));
            }

            var projectContext = new ExtractorContext
            {
                ProjectName = context.ProjectName,
                RootPath = context.RootPath,
                DotnetProject = project.Name,
                FoundationalKnowledge = context.FoundationalKnowledge
            };

            foreach (var document in project.Documents)
            {
                try
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync(ct2);
                    if (syntaxTree is null) continue;

                    var model = compilation.GetSemanticModel(syntaxTree);

                    // Pass Document.FilePath as override — MSBuildWorkspace always knows
                    // the on-disk path even when SyntaxTree.FilePath is empty (common
                    // with older non-SDK .csproj projects).
                    var walker = new CodeGraphSyntaxWalker(projectContext, model,
                        filePathOverride: document.FilePath);
                    walker.Visit(await syntaxTree.GetRootAsync(ct2));
                    results.Add(walker.GetResult());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to analyze {File}",
                        document.FilePath ?? "(unknown)");
                }
            }
        });

        // Stash Roslyn diagnostics for VitalsAnalyzer to consume
        if (!diagnosticCounts.IsEmpty)
        {
            var lintResults = diagnosticCounts.ToDictionary(
                kvp => kvp.Key,
                kvp => new LintResult(kvp.Value.Errors, kvp.Value.Warnings),
                StringComparer.OrdinalIgnoreCase);
            _lintCache.Set(context.ProjectName, lintResults);
            _logger.LogInformation("Stashed Roslyn diagnostics for {Project}: {Count} files with issues",
                context.ProjectName, lintResults.Count);
        }

        // Detect framework from .csproj files next to the solution
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var metadata = DetectMetadata(solutionDir);

        _logger.LogInformation("Extraction complete: {NodeCount} results from {Solution} (framework: {Framework})",
            results.Count, Path.GetFileName(solutionPath), metadata.Framework ?? "unknown");

        // Attach metadata to the first result so the pipeline can pick it up
        var resultList = results.ToList();
        if (resultList.Count > 0)
            resultList[0] = resultList[0] with { Metadata = metadata };

        return resultList;
    }

    private async Task RestoreNuGetPackagesAsync(string solutionPath, CancellationToken ct)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        // Check if any project uses packages.config (old-style NuGet)
        var hasPackagesConfig = Directory.GetFiles(solutionDir, "packages.config", SearchOption.AllDirectories).Length > 0;

        if (hasPackagesConfig)
        {
            // Old-style packages.config projects need nuget.exe restore
            var nugetConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "NuGet", "NuGet.Config");
            var configArg = File.Exists(nugetConfigPath) ? $" -ConfigFile \"{nugetConfigPath}\"" : "";
            await RunRestoreCommandAsync("nuget", $"restore \"{solutionPath}\"{configArg}", solutionDir, ct);
        }
        else
        {
            // SDK-style projects use dotnet restore
            await RunRestoreCommandAsync("dotnet", $"restore \"{solutionPath}\"", solutionDir, ct);
        }
    }

    private async Task RunRestoreCommandAsync(string command, string arguments, string workingDir, CancellationToken ct)
    {
        _logger.LogInformation("Running {Command} {Arguments}", command, arguments);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            // Read output asynchronously to avoid deadlocks
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var errorOutput = await stderr;
                _logger.LogWarning("{Command} restore exited with code {ExitCode}: {Error}",
                    command, process.ExitCode, errorOutput);
            }
            else
            {
                _logger.LogInformation("{Command} restore completed successfully", command);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Command} restore failed — continuing without restore", command);
        }
    }

    private static ProjectMetadata DetectMetadata(string solutionDir)
    {
        var csprojFiles = Directory.GetFiles(solutionDir, "*.csproj", SearchOption.AllDirectories);
        var framework = DetectFrameworkFromCsproj(csprojFiles);
        return new ProjectMetadata("C#", framework);
    }

    private static string? DetectFrameworkFromCsproj(string[] csprojFiles)
    {
        foreach (var csproj in csprojFiles)
        {
            try
            {
                var doc = XDocument.Load(csproj);
                var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";

                if (sdk.Contains("Microsoft.NET.Sdk.Web"))
                    return "ASP.NET Web API";
                if (sdk.Contains("Microsoft.NET.Sdk.Worker"))
                    return "Worker Service";

                var packages = doc.Descendants("PackageReference")
                    .Select(p => p.Attribute("Include")?.Value ?? "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (packages.Any(p => p.Contains("MassTransit") || p.Contains("TcServiceStack.Queue")))
                    return "ASP.NET Web API + MassTransit";
                if (packages.Contains("Microsoft.AspNetCore.OpenApi"))
                    return "ASP.NET Web API";
            }
            catch
            {
                // Skip unparseable csproj files
            }
        }

        return ".NET";
    }
}
