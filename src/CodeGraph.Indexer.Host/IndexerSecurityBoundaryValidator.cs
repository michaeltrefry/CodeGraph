using CodeGraph.Extractors.CSharp;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Extractors;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeGraph.Indexer.Host;

internal static class IndexerSecurityBoundaryValidator
{
    internal const string Command = "--validate-untrusted-csharp-boundary";

    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], Command, StringComparison.Ordinal))
            return null;

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

        var analyzer = new SolutionAnalyzer(
            NullLogger<SolutionAnalyzer>.Instance,
            new LintResultCache(),
            new DiagnosticDetailCache());
        await analyzer.AnalyzeSolutionAsync(
            solutionPath,
            new ExtractorContext
            {
                ProjectName = "untrusted-boundary-validation",
                RootPath = Path.GetDirectoryName(solutionPath)!,
                RepositoryToolingTrust = RepositoryToolingTrust.Untrusted
            },
            CancellationToken.None);

        if (File.Exists(markerPath))
        {
            Console.Error.WriteLine("FAIL: untrusted repository tooling executed.");
            return 1;
        }

        Console.WriteLine("PASS: production indexer blocked untrusted repository tooling.");
        return 0;
    }
}
