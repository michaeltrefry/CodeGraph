using CodeGraph.Extractors.CSharp;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

public sealed class SolutionAnalyzerTrustPolicyTests
{
    [Fact]
    public async Task AnalyzeSolutionAsync_UntrustedMaliciousRestoreTarget_DoesNotExecute()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MaliciousRestore");
        var workingRoot = Path.Combine(Path.GetTempPath(), $"codegraph-malicious-restore-{Guid.NewGuid():N}");
        CopyDirectory(fixtureRoot, workingRoot);

        try
        {
            var markerPath = Path.Combine(workingRoot, "restore-payload-executed.txt");
            var analyzer = new SolutionAnalyzer(
                NullLogger<SolutionAnalyzer>.Instance,
                new LintResultCache(),
                new DiagnosticDetailCache());

            await analyzer.AnalyzeSolutionAsync(
                Path.Combine(workingRoot, "MaliciousRestore.slnx"),
                new ExtractorContext
                {
                    ProjectName = "MaliciousRestore",
                    RootPath = workingRoot
                },
                CancellationToken.None);

            File.Exists(markerPath).ShouldBeFalse(
                "untrusted repository-controlled BeforeTargets=Restore code must not execute");
        }
        finally
        {
            Directory.Delete(workingRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeSolutionAsync_ExplicitlyTrustedRepository_RunsSolutionTooling()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MaliciousRestore");
        var workingRoot = Path.Combine(Path.GetTempPath(), $"codegraph-trusted-restore-{Guid.NewGuid():N}");
        CopyDirectory(fixtureRoot, workingRoot);

        try
        {
            var markerPath = Path.Combine(workingRoot, "restore-payload-executed.txt");
            var analyzer = new SolutionAnalyzer(
                NullLogger<SolutionAnalyzer>.Instance,
                new LintResultCache(),
                new DiagnosticDetailCache());

            await analyzer.AnalyzeSolutionAsync(
                Path.Combine(workingRoot, "MaliciousRestore.slnx"),
                new ExtractorContext
                {
                    ProjectName = "MaliciousRestore",
                    RootPath = workingRoot,
                    RepositoryToolingTrust = RepositoryToolingTrust.Trusted
                },
                CancellationToken.None);

            File.Exists(markerPath).ShouldBeTrue(
                "explicit trust is the only policy path that enables repository-controlled tooling");
        }
        finally
        {
            Directory.Delete(workingRoot, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }
}
