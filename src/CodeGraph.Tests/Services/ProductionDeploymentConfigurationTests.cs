using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class ProductionDeploymentConfigurationTests
{
    [Fact]
    public void IndexerResourceTunablesUsedByComposeAreForwardedByDeployWorkflow()
    {
        var repositoryRoot = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(
            repositoryRoot, "deploy", "docker-compose.production.yml"));
        var workflow = File.ReadAllText(Path.Combine(
            repositoryRoot, ".github", "workflows", "deploy.yml"));
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "deploy", "README.md"));

        compose.ShouldContain("mem_limit: ${CODEGRAPH_INDEXER_MEMORY_LIMIT:-10g}");
        compose.ShouldContain("memswap_limit: ${CODEGRAPH_INDEXER_MEMORY_LIMIT:-10g}");
        readme.ShouldContain("CODEGRAPH_INDEXER_MEMORY_LIMIT=10g");
        string[] tunables =
        [
            "CODEGRAPH_INDEXER_CPUS",
            "CODEGRAPH_INDEXER_MEMORY_LIMIT",
            "CODEGRAPH_RUST_SEMANTIC_TIMEOUT_SECONDS",
            "CODEGRAPH_RUST_SEMANTIC_MAX_THREADS",
            "CODEGRAPH_RUST_SEMANTIC_STDERR_TAIL_CHARACTERS"
        ];

        foreach (var tunable in tunables)
        {
            compose.ShouldContain(tunable);
            workflow.ShouldContain($"\"{tunable}\"");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeGraph.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the CodeGraph repository root.");
    }
}
