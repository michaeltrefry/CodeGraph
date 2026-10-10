using System.Diagnostics;
using Shouldly;

namespace CodeGraph.Tests.Services;

// Runs deploy/verify-deployment.sh against a fake `docker` (and a fake `sleep` that marks the end of the
// stability window) so the script's pass/fail logic is exercised, not just its text.
public sealed class VerifyDeploymentScriptTests : IDisposable
{
    private const string Prefix = "ghcr.io/michaeltrefry/codegraph";
    private const string Tag = "abc123";

    private const string FakeDocker = """
        #!/bin/sh
        state="$FAKE_DOCKER_STATE"
        if [ "$1" = "compose" ]; then
          shift
          while [ "$1" = "-f" ]; do shift 2; done
          case "$1" in
            config) cat "$state/services" ;;
            ps)
              if [ "${2:-}" = "-a" ] && [ "${3:-}" = "-q" ]; then
                if [ -f "$state/$4" ]; then echo "cid-$4"; fi
              else
                echo "fake compose ps"
              fi ;;
            logs) echo "fake logs: $*" ;;
          esac
          exit 0
        fi
        if [ "$1" = "inspect" ]; then
          case "$3" in
            '{{.Config.Image}}'*)
              service="${4#cid-}"
              if [ -f "$state/slept" ] && [ -f "$state/$service.after" ]; then
                cat "$state/$service.after"
              else
                cat "$state/$service"
              fi ;;
          esac
          exit 0
        fi
        exit 1

        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "verify-deploy-" + Guid.NewGuid().ToString("N"));
    private readonly string _bin;
    private readonly string _state;

    public VerifyDeploymentScriptTests()
    {
        _bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        _state = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        if (OperatingSystem.IsWindows())
            return;

        AddTool("docker", FakeDocker);
        AddTool("sleep", "#!/bin/sh\ntouch \"$FAKE_DOCKER_STATE/slept\"\n");
        File.WriteAllText(Path.Combine(_state, "services"), "codegraph-api\ncodegraph-web\n");
    }

    private static string Healthy(string service, int restarts = 0) =>
        $"{Prefix}-{service.Replace("codegraph-", "")}:{Tag}|running|false|{restarts}|healthy";

    private void SetState(string service, string state, string? afterStabilityWindow = null)
    {
        File.WriteAllText(Path.Combine(_state, service), state + "\n");
        if (afterStabilityWindow is not null)
            File.WriteAllText(Path.Combine(_state, service + ".after"), afterStabilityWindow + "\n");
    }

    [Fact]
    public void AllServicesHealthyOnTheDeployedTag_Passes()
    {
        if (OperatingSystem.IsWindows())
            return;
        SetState("codegraph-api", Healthy("codegraph-api"));
        SetState("codegraph-web", Healthy("codegraph-web"));

        var (exitCode, output) = Run();

        exitCode.ShouldBe(0, output);
        output.ShouldContain("Deployment verified", customMessage: output);
        output.ShouldNotContain("::error::", customMessage: output);
    }

    [Theory]
    [InlineData("ghcr.io/michaeltrefry/codegraph-api:oldtag")]
    [InlineData("ghcr.io/someone-else/codegraph-api:abc123")]
    public void ServiceOnWrongImage_FailsVisibly(string image)
    {
        if (OperatingSystem.IsWindows())
            return;
        SetState("codegraph-api", $"{image}|running|false|0|healthy");
        SetState("codegraph-web", Healthy("codegraph-web"));

        var (exitCode, output) = Run();

        exitCode.ShouldBe(1, output);
        output.ShouldContain($"::error::Services not healthy on {Tag}", customMessage: output);
        output.ShouldContain($"waiting: codegraph-api (image={image}", customMessage: output);
    }

    [Fact]
    public void ServiceReachingMaxRestarts_FailsAsCrashLoop()
    {
        if (OperatingSystem.IsWindows())
            return;
        SetState("codegraph-api", $"{Prefix}-api:{Tag}|restarting|true|3|unhealthy");
        SetState("codegraph-web", Healthy("codegraph-web"));

        var (exitCode, output) = Run();

        exitCode.ShouldBe(1, output);
        output.ShouldContain("::error::codegraph-api restarted 3 times (crash loop)", customMessage: output);
    }

    [Fact]
    public void RestartCountOfAHealthyServiceDoesNotLeakIntoTheNextServiceCheck()
    {
        if (OperatingSystem.IsWindows())
            return;
        // api is healthy despite earlier restarts; web has no container yet. web must time out, not be
        // reported as a crash loop using api's restart count.
        SetState("codegraph-api", Healthy("codegraph-api", restarts: 4));

        var (exitCode, output) = Run();

        exitCode.ShouldBe(1, output);
        output.ShouldContain($"::error::Services not healthy on {Tag} after 0s: codegraph-web", customMessage: output);
        output.ShouldNotContain("crash loop", customMessage: output);
    }

    [Fact]
    public void RestartDuringStabilityWindow_FailsVisibly()
    {
        if (OperatingSystem.IsWindows())
            return;
        SetState("codegraph-api", Healthy("codegraph-api"), afterStabilityWindow: Healthy("codegraph-api", restarts: 1));
        SetState("codegraph-web", Healthy("codegraph-web"));

        var (exitCode, output) = Run();

        exitCode.ShouldBe(1, output);
        output.ShouldContain("::error::codegraph-api restarted during the stability window", customMessage: output);
    }

    private (int ExitCode, string Output) Run()
    {
        var script = Path.Combine(FindRepositoryRoot(), "deploy", "verify-deployment.sh");
        var shell = File.Exists("/bin/dash") ? "/bin/dash" : "/bin/sh";
        var startInfo = new ProcessStartInfo(shell, [script])
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["PATH"] = _bin + ":" + Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["FAKE_DOCKER_STATE"] = _state;
        startInfo.Environment["IMAGE_PREFIX"] = Prefix;
        startInfo.Environment["IMAGE_TAG"] = Tag;
        startInfo.Environment["CODEGRAPH_DEPLOY_VERIFY_TIMEOUT_SECONDS"] = "0";
        startInfo.Environment["CODEGRAPH_DEPLOY_VERIFY_POLL_SECONDS"] = "0";
        startInfo.Environment["CODEGRAPH_DEPLOY_VERIFY_STABLE_SECONDS"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("verify-deployment.sh did not exit within 30s");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private void AddTool(string name, string content)
    {
        var path = Path.Combine(_bin, name);
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
