using System.Diagnostics;
using System.Text.Json;
using CodeGraph.Extractors.Rust;
using CodeGraph.Services;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

[Collection("Process environment")]
public sealed class RustProjectAnalyzerCapabilityTests
{
    [Fact]
    public async Task AnalyzeProjectAsync_MissingTools_ThrowsObservableCapabilityFailure()
    {
        using var fixture = new CargoFixture();
        using var path = new PathScope(fixture.ToolDirectory);
        var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);

        var error = await Should.ThrowAsync<RustSemanticIndexingException>(() =>
            analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context));

        error.FailureCode.ShouldBe("rust_semantic_tools_unavailable");
        error.Message.ShouldContain("rust-analyzer: False");
        error.Message.ShouldContain("scip: False");
    }

    [Fact]
    public async Task AnalyzeProjectAsync_BrokenGenerator_ThrowsObservableCapabilityFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        fixture.AddTool("rust-analyzer", "#!/bin/sh\necho generation-broke >&2\nexit 7\n");
        fixture.AddTool("scip", "#!/bin/sh\nexit 0\n");
        using var path = new PathScope(fixture.ToolDirectory);
        var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);

        var error = await Should.ThrowAsync<RustSemanticIndexingException>(() =>
            analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context));

        error.FailureCode.ShouldBe("rust_analyzer_scip_failed");
        error.Message.ShouldContain("generation-broke");
        error.Message.ShouldContain("7");
    }

    [Fact]
    public async Task AnalyzeProjectAsync_ExcludesDependencyDocumentsAndBoundsWorkerThreads()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        var argumentsPath = Path.Combine(fixture.RootPath, "rust-analyzer.args");
        var jsonPath = Path.Combine(fixture.RootPath, "scip.json");
        await File.WriteAllTextAsync(jsonPath, ValidScipJson);
        fixture.AddTool(
            "rust-analyzer",
            "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$CODEGRAPH_TEST_ARGS\"\nout=\"\"\nwhile [ \"$#\" -gt 0 ]; do\n  if [ \"$1\" = \"--output\" ]; then out=\"$2\"; shift 2; else shift; fi\ndone\n: > \"$out\"\n");
        fixture.AddTool("scip", "#!/bin/sh\n/bin/cat \"$CODEGRAPH_TEST_SCIP_JSON\"\n");
        using var path = new PathScope(fixture.ToolDirectory);
        using var argumentsVariable = new EnvironmentVariableScope("CODEGRAPH_TEST_ARGS", argumentsPath);
        using var jsonVariable = new EnvironmentVariableScope("CODEGRAPH_TEST_SCIP_JSON", jsonPath);
        var analyzer = new RustProjectAnalyzer(
            NullLogger<RustProjectAnalyzer>.Instance,
            Options.Create(new IndexingOptions { RustSemanticMaxThreads = 3 }));

        var results = await analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context);

        results.SelectMany(result => result.Nodes).ShouldNotBeEmpty();
        var arguments = await File.ReadAllLinesAsync(argumentsPath);
        arguments.ShouldContain("--exclude-vendored-libraries");
        var threadFlag = Array.IndexOf(arguments, "--num-threads");
        threadFlag.ShouldBeGreaterThanOrEqualTo(0);
        arguments[threadFlag + 1].ShouldBe("3");
    }

    [Fact]
    public async Task AnalyzeProjectAsync_BrokenConverter_ThrowsObservableCapabilityFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        fixture.AddTool("rust-analyzer", "#!/bin/sh\nout=\"\"\nwhile [ \"$#\" -gt 0 ]; do\n  if [ \"$1\" = \"--output\" ]; then out=\"$2\"; shift 2; else shift; fi\ndone\n: > \"$out\"\n");
        fixture.AddTool("scip", "#!/bin/sh\necho conversion-broke >&2\nexit 9\n");
        using var path = new PathScope(fixture.ToolDirectory);
        var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);

        var error = await Should.ThrowAsync<RustSemanticIndexingException>(() =>
            analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context));

        error.FailureCode.ShouldBe("scip_print_failed");
        error.Message.ShouldContain("conversion-broke");
        error.Message.ShouldContain("9");
    }

    [Fact]
    public async Task AnalyzeProjectAsync_StaleExistingIndexWithEmptyPath_DoesNotBypassLiveGeneration()
    {
        using var fixture = new CargoFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.RootPath, "index.scip.json"),
            ValidScipJson);
        using var path = new PathScope(fixture.ToolDirectory);
        var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);

        var error = await Should.ThrowAsync<RustSemanticIndexingException>(() =>
            analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context));

        error.FailureCode.ShouldBe("rust_semantic_tools_unavailable");
    }

    [Fact]
    public async Task AnalyzeProjectAsync_GeneratorSuccessWithoutOutput_ThrowsObservableFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        fixture.AddTool("rust-analyzer", "#!/bin/sh\nexit 0\n");
        fixture.AddTool("scip", "#!/bin/sh\nexit 0\n");
        using var path = new PathScope(fixture.ToolDirectory);
        var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);

        var error = await Should.ThrowAsync<RustSemanticIndexingException>(() =>
            analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context));

        error.FailureCode.ShouldBe("rust_analyzer_scip_missing_output");
    }

    [Fact]
    public async Task AnalyzeProjectAsync_EmptyConversion_ThrowsObservableFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        fixture.AddSuccessfulGenerator();
        fixture.AddTool("scip", "#!/bin/sh\nexit 0\n");
        using var path = new PathScope(fixture.ToolDirectory);
        var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);

        var error = await Should.ThrowAsync<RustSemanticIndexingException>(() =>
            analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context));

        error.FailureCode.ShouldBe("scip_print_empty");
    }

    [Fact]
    public async Task AnalyzeProjectAsync_InvalidConvertedJson_ThrowsStructuredImportFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        fixture.AddSuccessfulGenerator();
        fixture.AddTool("scip", "#!/bin/sh\nprintf 'not-json'\n");
        using var path = new PathScope(fixture.ToolDirectory);
        var analyzer = new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance);

        var error = await Should.ThrowAsync<RustSemanticIndexingException>(() =>
            analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context));

        error.FailureCode.ShouldBe("rust_semantic_import_failed");
        error.InnerException.ShouldBeAssignableTo<JsonException>();
    }

    [Fact]
    public async Task AnalyzeProjectAsync_Timeout_KillsAndDrainsAnalyzerProcessTree()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        var childPidPath = Path.Combine(fixture.RootPath, "timeout-child.pid");
        fixture.AddTool("rust-analyzer", LongRunningGeneratorScript);
        fixture.AddTool("scip", "#!/bin/sh\nexit 0\n");
        using var path = new PathScope(fixture.ToolDirectory);
        using var pidVariable = new EnvironmentVariableScope("CODEGRAPH_TEST_CHILD_PID", childPidPath);
        var analyzer = new RustProjectAnalyzer(
            NullLogger<RustProjectAnalyzer>.Instance,
            Options.Create(new IndexingOptions
            {
                RustSemanticCommandTimeoutSeconds = 1,
                RustSemanticStderrTailCharacters = 1024
            }));

        var error = await Should.ThrowAsync<RustSemanticIndexingException>(() =>
            analyzer.AnalyzeProjectAsync(fixture.ManifestPath, fixture.Context));

        error.FailureCode.ShouldBe("rust_semantic_command_timeout");
        error.Message.ShouldContain("1-second timeout");
        error.Message.ShouldContain("generator-still-running");
        var childPid = await ReadPidAsync(childPidPath);
        await AssertProcessExitedAsync(childPid);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_CallerCancellation_KillsTreeAndPreservesCancellation()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        var childPidPath = Path.Combine(fixture.RootPath, "cancel-child.pid");
        fixture.AddTool("rust-analyzer", LongRunningGeneratorScript);
        fixture.AddTool("scip", "#!/bin/sh\nexit 0\n");
        using var path = new PathScope(fixture.ToolDirectory);
        using var pidVariable = new EnvironmentVariableScope("CODEGRAPH_TEST_CHILD_PID", childPidPath);
        using var cancellation = new CancellationTokenSource();
        var analyzer = new RustProjectAnalyzer(
            NullLogger<RustProjectAnalyzer>.Instance,
            TimeSpan.FromSeconds(30));

        var analysis = analyzer.AnalyzeProjectAsync(
            fixture.ManifestPath,
            fixture.Context,
            cancellation.Token);
        var childPid = await ReadPidAsync(childPidPath);
        cancellation.Cancel();

        var error = await Should.ThrowAsync<OperationCanceledException>(() => analysis);
        error.CancellationToken.ShouldBe(cancellation.Token);
        await AssertProcessExitedAsync(childPid);
    }

    [Fact]
    public async Task AnalyzeProjectAsync_CallerCancellation_AfterAnalyzerExit_KillsEscapedDescendantAndReturnsPromptly()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new CargoFixture();
        var childPidPath = Path.Combine(fixture.RootPath, "escaped-child.pid");
        fixture.AddTool("rust-analyzer", EscapedDescendantGeneratorScript);
        fixture.AddTool("scip", "#!/bin/sh\nexit 0\n");
        using var path = new PathScope(fixture.ToolDirectory);
        using var pidVariable = new EnvironmentVariableScope("CODEGRAPH_TEST_CHILD_PID", childPidPath);
        using var cancellation = new CancellationTokenSource();
        var analyzer = new RustProjectAnalyzer(
            NullLogger<RustProjectAnalyzer>.Instance,
            TimeSpan.FromSeconds(30));

        var analysis = analyzer.AnalyzeProjectAsync(
            fixture.ManifestPath,
            fixture.Context,
            cancellation.Token);
        var childPid = await ReadPidAsync(childPidPath);
        cancellation.Cancel();

        try
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(() =>
                analysis.WaitAsync(TimeSpan.FromSeconds(2)));
            error.CancellationToken.ShouldBe(cancellation.Token);
            await AssertProcessExitedAsync(childPid);
        }
        finally
        {
            TryKillProcess(childPid);
        }
    }

    private static async Task<int> ReadPidAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        File.Exists(path).ShouldBeTrue($"Expected child PID file '{path}'.");
        return int.Parse(await File.ReadAllTextAsync(path));
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new ShouldAssertException($"Child process {processId} survived analyzer teardown.");
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
    }

    private const string LongRunningGeneratorScript =
        "#!/bin/sh\n" +
        "echo generator-still-running >&2\n" +
        "/bin/sleep 30 &\n" +
        "child=$!\n" +
        "printf '%s' \"$child\" > \"$CODEGRAPH_TEST_CHILD_PID\"\n" +
        "wait \"$child\"\n";

    private const string EscapedDescendantGeneratorScript =
        "#!/bin/sh\n" +
        "out=\"\"\n" +
        "while [ \"$#\" -gt 0 ]; do\n" +
        "  if [ \"$1\" = \"--output\" ]; then out=\"$2\"; shift 2; else shift; fi\n" +
        "done\n" +
        ": > \"$out\"\n" +
        "/bin/sleep 30 &\n" +
        "child=$!\n" +
        "printf '%s' \"$child\" > \"$CODEGRAPH_TEST_CHILD_PID\"\n" +
        "exit 0\n";

    private const string ValidScipJson = """
        {
          "documents": [
            {
              "language": "rust",
              "relativePath": "src/lib.rs",
              "symbols": [
                { "symbol": "rust-analyzer cargo stale 0.1.0 stale/value().", "kind": "Function", "displayName": "value" }
              ],
              "occurrences": [
                { "symbol": "rust-analyzer cargo stale 0.1.0 stale/value().", "symbolRoles": 1, "range": [0, 7, 12] }
              ]
            }
          ]
        }
        """;

    private sealed class CargoFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), $"codegraph-rust-capability-{Guid.NewGuid():N}");

        public string ToolDirectory => Path.Combine(_root, "tools");
        public string RootPath => _root;
        public string ManifestPath => Path.Combine(_root, "Cargo.toml");
        public ExtractorContext Context => new()
        {
            ProjectName = "RustCapabilityFixture",
            RootPath = _root
        };

        public CargoFixture()
        {
            Directory.CreateDirectory(ToolDirectory);
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            File.WriteAllText(ManifestPath,
                "[package]\nname = \"rust_capability_fixture\"\nversion = \"0.1.0\"\nedition = \"2024\"\n");
            File.WriteAllText(Path.Combine(_root, "src", "lib.rs"), "pub fn value() -> i32 { 1 }\n");
        }

        public void AddTool(string name, string content)
        {
            var path = Path.Combine(ToolDirectory, name);
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public void AddSuccessfulGenerator() => AddTool(
            "rust-analyzer",
            "#!/bin/sh\nout=\"\"\nwhile [ \"$#\" -gt 0 ]; do\n  if [ \"$1\" = \"--output\" ]; then out=\"$2\"; shift 2; else shift; fi\ndone\n: > \"$out\"\n");

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class PathScope : IDisposable
    {
        private readonly string? _original = Environment.GetEnvironmentVariable("PATH");

        public PathScope(string path) => Environment.SetEnvironmentVariable("PATH", path);

        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _original);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
