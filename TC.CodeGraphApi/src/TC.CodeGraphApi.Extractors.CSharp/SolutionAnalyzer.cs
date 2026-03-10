using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Extractors.CSharp;

public class SolutionAnalyzer : ISolutionAnalyzer
{
    private static int _msBuildRegistered;
    private readonly ILogger<SolutionAnalyzer> _logger;

    public SolutionAnalyzer(ILogger<SolutionAnalyzer> logger)
    {
        _logger = logger;
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

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
            _logger.LogWarning("Workspace warning: {Message}", e.Diagnostic.Message);

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        var results = new ConcurrentBag<ExtractionResult>();

        _logger.LogInformation("Analyzing {Count} projects in solution", solution.Projects.Count());

        await Parallel.ForEachAsync(solution.Projects, ct, async (project, ct2) =>
        {
            var compilation = await project.GetCompilationAsync(ct2);
            if (compilation is null)
            {
                _logger.LogWarning("Could not compile project {Project}", project.Name);
                return;
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                try
                {
                    var model = compilation.GetSemanticModel(syntaxTree);
                    var walker = new CodeGraphSyntaxWalker(context, model);
                    walker.Visit(await syntaxTree.GetRootAsync(ct2));
                    results.Add(walker.GetResult());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to analyze {File}", syntaxTree.FilePath);
                }
            }
        });

        _logger.LogInformation("Extraction complete: {NodeCount} results from {Solution}",
            results.Count, Path.GetFileName(solutionPath));

        return results.ToList();
    }
}
