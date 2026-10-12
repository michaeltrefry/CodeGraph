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

    [Fact]
    public void TrustedDotnetRepositoriesUsedByComposeAreForwardedByDeployWorkflow()
    {
        var repositoryRoot = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(repositoryRoot, "deploy", "docker-compose.production.yml"));
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "deploy.yml"));

        compose.ShouldContain("${CODEGRAPH_TRUSTED_DOTNET_REPOSITORIES:-}");
        PassThroughNames(workflow).ShouldContain("CODEGRAPH_TRUSTED_DOTNET_REPOSITORIES");
    }

    [Fact]
    public void EverySshAndScpInvocationUsesKeepaliveAndConnectTimeout()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "deploy.yml"));
        var commands = LogicalShellLines(workflow)
            .Where(line => line.StartsWith("ssh ", StringComparison.Ordinal) ||
                           line.StartsWith("scp ", StringComparison.Ordinal))
            .ToList();

        commands.Count.ShouldBeGreaterThanOrEqualTo(7);
        foreach (var command in commands)
        {
            command.ShouldContain("-o ConnectTimeout=", customMessage: command);
            command.ShouldContain("-o ServerAliveInterval=", customMessage: command);
            command.ShouldContain("-o ServerAliveCountMax=", customMessage: command);
            command.ShouldContain("-o BatchMode=yes", customMessage: command);
            command.ShouldContain("-o StrictHostKeyChecking=yes", customMessage: command);
        }
    }

    [Fact]
    public void DeployJobVerifiesHealthAfterComposeUpAndFailsTheJobOnError()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "deploy.yml"));
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "deploy", "verify-deployment.sh"));

        var deployStep = workflow.IndexOf("- name: Deploy over SSH", StringComparison.Ordinal);
        var verifyStep = workflow.IndexOf("- name: Verify deployment health", StringComparison.Ordinal);
        deployStep.ShouldBeGreaterThan(0);
        verifyStep.ShouldBeGreaterThan(deployStep);
        var verify = workflow[verifyStep..];
        verify.ShouldContain("if: steps.deploy-gate.outputs.enabled == 'true'");
        verify.ShouldContain("timeout-minutes:");
        verify.ShouldContain("IMAGE_TAG: ${{ needs.promote.outputs.image-tag }}");
        verify.ShouldContain("sh deploy/verify-deployment.sh");
        workflow.ShouldContain("deploy/verify-deployment.sh \"$CODEGRAPH_DEPLOY_USER@$CODEGRAPH_DEPLOY_HOST:$CODEGRAPH_DEPLOY_PATH/deploy/\"");

        // Bounded wait for the new tag + healthy + not restarting; diagnostics and a non-zero exit on failure.
        script.ShouldContain("set -eu");
        script.ShouldContain("CODEGRAPH_DEPLOY_VERIFY_TIMEOUT_SECONDS:-600");
        script.ShouldContain("\"$IMAGE_PREFIX\"-*\":$IMAGE_TAG\")");
        script.ShouldContain("[ \"$health\" = \"healthy\" ]");
        script.ShouldContain("[ \"$restarting\" = \"false\" ]");
        script.ShouldContain("compose ps -a");
        script.ShouldContain("compose logs --no-color --tail=\"$log_lines\"");
        script.ShouldContain("exit 1");
    }

    [Fact]
    public void DeployRunsOnlyAfterCiSucceedsOnMainAndDeploysTheTestedCommit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var ci = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml")).Replace("\r\n", "\n");
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "deploy.yml")).Replace("\r\n", "\n");

        // CI runs on PRs and on every push to main, without cancelling main runs.
        ci.ShouldStartWith("name: CI\n");
        ci.ShouldContain("on:\n  pull_request:\n");
        ci.ShouldContain("\n  push:\n    branches:\n      - main\n");
        ci.ShouldContain("cancel-in-progress: ${{ github.event_name == 'pull_request' }}");

        // Deploy is triggered by a completed CI run on main (or manually), never by a raw push.
        var on = workflow[workflow.IndexOf("\non:\n", StringComparison.Ordinal)..workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];
        on.ShouldContain("  workflow_dispatch:\n");
        on.ShouldContain("  workflow_run:\n    workflows:\n      - CI\n    types:\n      - completed\n    branches:\n      - main\n");
        on.ShouldNotContain("  push:");

        // Promote (and therefore deploy, which needs it) runs only for a successful push-to-main CI run.
        var promote = PromoteJob(workflow);
        promote.ShouldContain("if: github.event_name == 'workflow_dispatch' || (github.event.workflow_run.conclusion == 'success' && github.event.workflow_run.event == 'push')");
        workflow[workflow.IndexOf("  deploy:", StringComparison.Ordinal)..].ShouldContain("needs: promote");

        // The tested commit is what is checked out, tagged, and deployed.
        workflow.ShouldContain("DEPLOY_SHA: ${{ github.event_name == 'workflow_run' && github.event.workflow_run.head_sha || github.sha }}");
        workflow.ShouldNotContain("${{ github.sha }}");
        workflow.ShouldNotContain("${GITHUB_SHA}");
        promote.ShouldContain("run: echo \"image-tag=${DEPLOY_SHA}\"");
        var checkouts = LogicalShellLines(workflow).Count(line => line == "uses: actions/checkout@v4");
        checkouts.ShouldBe(1);
        workflow.Split("uses: actions/checkout@v4\n        with:\n          ref: ${{ env.DEPLOY_SHA }}").Length.ShouldBe(checkouts + 1);
    }

    [Fact]
    public void DeployNeverBuildsImages()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "deploy.yml"));

        workflow.ShouldNotContain("build-push-action");
        workflow.ShouldNotMatch(@"(?s).*\bdocker( buildx)? (build|bake)\s.*");
        workflow.ShouldNotMatch(@"(?s).*\bdocker( compose)?\b[^\n]* build\b.*");
        workflow.ShouldNotContain("cache-from");
    }

    [Fact]
    public void DeployDeploysTheImagesCiPushedForTheTestedCommit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "deploy.yml")).Replace("\r\n", "\n");
        var promote = PromoteJob(workflow);

        // Same registry prefix CI pushes to.
        workflow.ShouldContain("IMAGE_PREFIX: ghcr.io/${{ github.repository_owner }}/codegraph\n");

        // Even a manual dispatch deploys only a commit with a successful CI push run.
        promote.ShouldContain("actions/workflows/ci.yml/runs?head_sha=$DEPLOY_SHA&event=push&status=success");
        promote.ShouldContain("if [ \"$runs\" -lt 1 ]; then");

        // Every image is resolved by the tested SHA (a missing image fails the retag), and that SHA is the deployed tag.
        promote.ShouldContain("for image in api jobs indexer memory metrics web; do");
        LogicalShellLines(promote).ShouldContain(
            "docker buildx imagetools create --tag \"$IMAGE_PREFIX-$image:latest\" \"$IMAGE_PREFIX-$image:$DEPLOY_SHA\"");
        var deploy = workflow[workflow.IndexOf("- name: Deploy over SSH", StringComparison.Ordinal)..workflow.IndexOf("- name: Verify deployment health", StringComparison.Ordinal)];
        deploy.ShouldContain("IMAGE_TAG: ${{ needs.promote.outputs.image-tag }}");
        deploy.ShouldContain("IMAGE_TAG='$IMAGE_TAG' docker compose -f docker-compose.yml -f docker-compose.production.yml pull");
    }

    [Fact]
    public void CiPushesEveryImageTaggedWithTheCommitOnlyOnPushToMain()
    {
        var ci = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml")).Replace("\r\n", "\n");
        var job = ci[ci.IndexOf("\n  docker-build:\n", StringComparison.Ordinal)..ci.IndexOf("\n  rust-semantic-arm64:\n", StringComparison.Ordinal)];

        foreach (var image in new[] { "api", "jobs", "indexer", "memory", "metrics", "web" })
            job.ShouldContain($"          - image: {image}\n");
        job.ShouldContain("IMAGE: ghcr.io/${{ github.repository_owner }}/codegraph-${{ matrix.image }}\n");
        job.ShouldContain("packages: write");

        // The build step never pushes; only the push step does, only for push events (never PRs), and
        // only after the indexer image passed its semantic-indexing validation.
        var build = job[job.IndexOf("- name: Build image", StringComparison.Ordinal)..job.IndexOf("- name: Validate Rust semantic indexing", StringComparison.Ordinal)];
        build.ShouldContain("push: false");
        build.ShouldContain("cache-to: ${{ github.event_name == 'push' && ");
        var push = job[job.IndexOf("- name: Push image tagged with the tested commit", StringComparison.Ordinal)..];
        push.ShouldContain("if: github.event_name == 'push'\n");
        push.ShouldContain("push: true");
        push.ShouldContain("tags: ${{ env.IMAGE }}:${{ github.sha }}\n");
        job.Split("push: true").Length.ShouldBe(2);
    }

    private static string PromoteJob(string workflow) =>
        workflow[workflow.IndexOf("  promote:", StringComparison.Ordinal)..workflow.IndexOf("  deploy:", StringComparison.Ordinal)];

    [Fact]
    public void DisabledDeployGateIsReportedAsSkippedWithoutFailing()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "deploy.yml"));
        var gate = workflow[workflow.IndexOf("- name: Check deployment gate", StringComparison.Ordinal)..workflow.IndexOf("- name: Checkout\n        if: steps.deploy-gate", StringComparison.Ordinal)];

        gate.ShouldContain("::notice title=Deployment skipped::");
        gate.ShouldContain("## Deployment skipped");
        gate.ShouldContain("$GITHUB_STEP_SUMMARY");
        gate.ShouldNotContain("exit 1");
    }

    [Fact]
    public void EveryProductionServiceDefinesAHealthcheck()
    {
        var repositoryRoot = FindRepositoryRoot();
        var baseCompose = File.ReadAllText(Path.Combine(repositoryRoot, "docker-compose.yml"));
        var compose = File.ReadAllText(Path.Combine(repositoryRoot, "deploy", "docker-compose.production.yml"));
        var expected = new Dictionary<string, string>
        {
            ["codegraph-api"] = "\"curl\", \"-fsS\", \"-o\", \"/dev/null\", \"http://127.0.0.1:5037/health\"",
            ["codegraph-indexer"] = "\"curl\", \"-fsS\", \"-o\", \"/dev/null\", \"http://127.0.0.1:5042/health\"",
            ["codegraph-memory"] = "/dev/tcp/127.0.0.1/5039",
            ["codegraph-metrics"] = "/dev/tcp/127.0.0.1/5041",
            ["jobs"] = $"\"test\", \"-f\", \"/tmp/{CodeGraph.Jobs.JobsReadinessMarker.FileName}\"",
            ["codegraph-web"] = "\"wget\", \"-q\", \"-O\", \"/dev/null\", \"http://127.0.0.1/\""
        };

        ServiceNames(baseCompose).ShouldBe(expected.Keys, ignoreOrder: true);
        ServiceNames(compose).ShouldBe(expected.Keys, ignoreOrder: true);
        foreach (var (service, probe) in expected)
        {
            var block = ServiceBlock(compose, service);
            block.ShouldContain("    healthcheck:", customMessage: service);
            block.ShouldContain(probe, customMessage: service);
            block.ShouldContain("      start_period:", customMessage: service);
        }
    }

    private static IEnumerable<string> PassThroughNames(string workflow)
    {
        var start = workflow.IndexOf("pass_through_names = {", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var end = workflow.IndexOf('}', start);
        return workflow[start..end]
            .Split('\n')
            .Select(line => line.Trim().TrimEnd(','))
            .Where(line => line.StartsWith('"'))
            .Select(line => line.Trim('"'));
    }

    // Joins backslash-continued lines so multi-line ssh invocations are checked as one command.
    private static IEnumerable<string> LogicalShellLines(string text)
    {
        var current = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.EndsWith('\\'))
            {
                current += line[..^1] + " ";
                continue;
            }

            yield return current + line;
            current = "";
        }
    }

    private static IEnumerable<string> ServiceNames(string compose)
    {
        var lines = compose.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, "services:");
        start.ShouldBeGreaterThanOrEqualTo(0);
        foreach (var line in lines.Skip(start + 1))
        {
            if (line.Length > 0 && line[0] != ' ')
                yield break;
            if (line.Length > 2 && line[..2] == "  " && line[2] != ' ' && line.TrimEnd().EndsWith(':'))
                yield return line.Trim().TrimEnd(':');
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
