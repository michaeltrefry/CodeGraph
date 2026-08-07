using CodeGraph.Models.Messages;
using CodeGraph.Services;

namespace CodeGraph.Indexer.Host;

internal static class IndexerSecurityBoundaryValidator
{
    internal const string Command = "--validate-untrusted-csharp-boundary";

    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Command, StringComparison.Ordinal);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine($"Usage: {Command} <solution-path> <marker-path>");
            return 64;
        }

        var solutionPath = Path.GetFullPath(args[1]);
        var markerPath = Path.GetFullPath(args[2]);
        if (!File.Exists(solutionPath))
        {
            Console.Error.WriteLine("Boundary validation solution does not exist.");
            return 66;
        }

        if (File.Exists(markerPath))
        {
            Console.Error.WriteLine("Boundary validation requires a marker path that does not exist.");
            return 64;
        }

        var repositoryPath = Path.GetDirectoryName(solutionPath)!;
        var projectService = services.GetRequiredService<IProjectService>();
        await projectService.ProcessRepository(new ProcessRepository
        {
            Name = Path.GetFileName(repositoryPath),
            RepoUrl = repositoryPath,
            ShouldIndex = true,
            ShouldAnalyze = false,
            SkipIfUpToDate = false,
            IncludeAllSource = true,
            ShouldComputeVitals = false
        });

        if (File.Exists(markerPath))
        {
            Console.Error.WriteLine("FAIL: untrusted repository tooling executed.");
            return 1;
        }

        Console.WriteLine("PASS: production indexer blocked untrusted repository tooling.");
        return 0;
    }
}
