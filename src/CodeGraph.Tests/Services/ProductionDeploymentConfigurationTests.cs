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
            "CODEGRAPH_RUST_SEMANTIC_STDERR_TAIL_CHARACTERS",
            "CODEGRAPH_RUST_SEMANTIC_CRATE_GROUP_SIZE",
            "CODEGRAPH_RUST_SEMANTIC_GROUP_TIMEOUT_SECONDS",
            "CODEGRAPH_RUST_SEMANTIC_WORKSPACE_TIMEOUT_SECONDS",
            "CODEGRAPH_RUST_SEMANTIC_GROUP_MEMORY_LIMIT_MB",
            "CODEGRAPH_RUST_SEMANTIC_BUILD_SCRIPTS",
            "CODEGRAPH_RUST_SEMANTIC_PROC_MACROS",
            "CODEGRAPH_BRANCH_OVERLAY_TTL_HOURS",
            "CODEGRAPH_BRANCH_OVERLAY_SWEEP_INTERVAL_MINUTES"
        ];

        foreach (var tunable in tunables)
        {
            compose.ShouldContain(tunable);
            workflow.ShouldContain($"\"{tunable}\"");
        }
    }

    [Fact]
    public void BranchOverlaySettingsAreWiredOnEveryServiceHostingOverlays()
    {
        var repositoryRoot = FindRepositoryRoot();
        string[] expected =
        [
            "CodeGraph__BranchOverlayOptions__TtlHours: ${CODEGRAPH_BRANCH_OVERLAY_TTL_HOURS:-72}",
            "CodeGraph__BranchOverlayOptions__SweepIntervalMinutes: ${CODEGRAPH_BRANCH_OVERLAY_SWEEP_INTERVAL_MINUTES:-15}"
        ];

        // The API (commit-pinned reads, expiry status) and the indexer (overlay builds and the TTL
        // sweep) both register branch overlays.
        foreach (var file in new[] { "docker-compose.yml", Path.Combine("deploy", "docker-compose.production.yml") })
        {
            var compose = File.ReadAllText(Path.Combine(repositoryRoot, file));
            foreach (var service in new[] { "codegraph-api", "codegraph-indexer" })
            {
                var block = ServiceBlock(compose, service);
                foreach (var line in expected)
                    block.ShouldContain(line, customMessage: $"{file} {service}");
            }
        }
    }

    private static string ServiceBlock(string compose, string service)
    {
        var lines = compose.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line == $"  {service}:");
        start.ShouldBeGreaterThanOrEqualTo(0, $"service {service} not found");
        var end = Array.FindIndex(lines, start + 1, line => line.Length > 2 && line[..2] == "  " && line[2] != ' ' && line.TrimEnd().EndsWith(':'));
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
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
