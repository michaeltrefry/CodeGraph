using System.Text.Json;
using CodeGraph.Extractors.Rust;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

[Collection("Process environment")]
public sealed class RustProjectAnalyzerCrateGroupTests
{
    [Fact]
    public async Task AnalyzeWorkspaceAsync_DefaultSettings_RunsScipWithoutBuildScriptsOrProcMacros()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        var analyzer = CreateAnalyzer(new IndexingOptions());

        var analysis = await analyzer.AnalyzeWorkspaceAsync(toolchain.ManifestPath, Context(toolchain));

        analysis.Groups.ShouldHaveSingleItem().Failure.ShouldBeNull();
        // Build scripts only ever run through `cargo check`; by default cargo is asked for metadata alone.
        (await File.ReadAllLinesAsync(toolchain.CargoLogPath))
            .ShouldAllBe(line => line.StartsWith("metadata ", StringComparison.Ordinal));
        var projectJson = toolchain.ProjectJsonFiles().ShouldHaveSingleItem();
        using var project = JsonDocument.Parse(projectJson);
        var crates = project.RootElement.GetProperty("crates").EnumerateArray().ToList();
        crates.ShouldNotBeEmpty();
        // rust-analyzer only expands proc-macros whose dylib the project names, and only runs
        // build scripts for Cargo (not rust-project.json) workspaces.
        crates.ShouldNotContain(crate => HasProperty(crate, "proc_macro_dylib_path"));
        crates.ShouldNotContain(crate => HasProperty(crate.GetProperty("env"), "OUT_DIR"));
        crates.Single(crate => crate.GetProperty("display_name").GetString() == "macros")
            .GetProperty("is_proc_macro").GetBoolean().ShouldBeTrue();
        File.Exists(Path.Combine(toolchain.RootPath, "rust-project.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task AnalyzeWorkspaceAsync_OneCratePerGroup_FailingGroupFallsAloneInDeterministicOrder()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "beta");
        var analyzer = CreateAnalyzer(new IndexingOptions { RustSemanticCrateGroupSize = 1 });

        var analysis = await analyzer.AnalyzeWorkspaceAsync(toolchain.ManifestPath, Context(toolchain));

        analysis.Groups.Select(group => string.Join(",", group.Crates)).ShouldBe(["alpha", "beta", "macros"]);
        analysis.Groups.Select(group => string.Join(",", group.SourceDirectories))
            .ShouldBe(["crates/alpha", "crates/beta", "crates/macros"]);
        toolchain.ProjectJsonFiles().Select(FakeRustToolchain.MemberCrates)
            .ShouldBe([["alpha"], ["beta"], ["macros"]]);

        analysis.Groups.Select(group => group.Index).ShouldBe([0, 1, 2]);
        analysis.Groups.ShouldAllBe(group => !group.Skipped && group.Duration > TimeSpan.Zero);
        analysis.Settings.ShouldBe(new RustSemanticSettingsSnapshot(1, 600, 3600, 8192, false, false));

        var beta = analysis.Groups[1];
        beta.Results.ShouldBeNull();
        var failure = beta.Failure.ShouldBeOfType<RustSemanticIndexingException>();
        failure.FailureCode.ShouldBe("rust_analyzer_scip_failed");
        failure.Message.ShouldContain("synthetic analyzer failure for beta");

        foreach (var group in new[] { analysis.Groups[0], analysis.Groups[2] })
        {
            group.Failure.ShouldBeNull();
            group.Results.ShouldNotBeNull().SelectMany(result => result.ProcessedFiles)
                .ShouldBe([$"crates/{group.Crates.Single()}/src/lib.rs"]);
        }
    }

    [Fact]
    public async Task AnalyzeProjectAsync_CrossGroupCall_LinksToDefinitionInOtherGroup()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        var analyzer = CreateAnalyzer(new IndexingOptions { RustSemanticCrateGroupSize = 1 });

        var results = await analyzer.AnalyzeProjectAsync(toolchain.ManifestPath, Context(toolchain));

        var nodes = results.SelectMany(result => result.Nodes).ToList();
        var edges = results.SelectMany(result => result.Edges).ToList();
        var caller = nodes.Single(node => node.Name == "alpha_entry");
        var target = nodes.Single(node => node.Name == "beta_compute" && !node.Properties.ContainsKey("scip_external"));
        edges.ShouldContain(edge =>
            edge.Type == EdgeType.CALLS &&
            edge.SourceQN == caller.QualifiedName &&
            edge.TargetQN == target.QualifiedName);
        nodes.ShouldNotContain(node => node.Properties.ContainsKey("scip_external") &&
                                       (string)node.Properties["scip_symbol"] ==
                                       FakeRustToolchain.Symbol("beta", "beta_compute"));
    }

    [Fact]
    public async Task AnalyzeProjectAsync_DocumentLocalSymbols_SameGraphForAnyGroupSize()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Every crate's document defines and calls its own `local 0`; how crates are grouped
        // must not change which nodes exist, what they are named, or what the edges connect.
        var oneCratePerGroup = await AnalyzeLocalsWorkspaceAsync(groupSize: 1);
        var allCratesInOneGroup = await AnalyzeLocalsWorkspaceAsync(groupSize: 8);

        oneCratePerGroup.Nodes.ShouldBe(allCratesInOneGroup.Nodes);
        oneCratePerGroup.Edges.ShouldBe(allCratesInOneGroup.Edges);
        oneCratePerGroup.Nodes.Count(node => node.Contains(":scip-local:")).ShouldBe(3);
        oneCratePerGroup.Edges.ShouldContain(
            $"CALLS {FakeRustToolchain.Symbol("alpha", "alpha_entry")} -> alpha_inner@crates/alpha/src/lib.rs");
    }

    private static async Task<(List<string> Nodes, List<string> Edges)> AnalyzeLocalsWorkspaceAsync(int groupSize)
    {
        using var toolchain = new FakeRustToolchain()
            .AddCrate("alpha", "pub fn alpha_entry() {}\n",
                LocalsScipDocument("alpha", "alpha_entry", FakeRustToolchain.Symbol("beta", "beta_compute")),
                dependencies: ["beta"])
            .AddCrate("beta", "pub fn beta_compute() {}\n", LocalsScipDocument("beta", "beta_compute"))
            .AddCrate("gamma", "pub fn gamma_run() {}\n", LocalsScipDocument("gamma", "gamma_run"));
        toolchain.Install();
        var analyzer = CreateAnalyzer(new IndexingOptions { RustSemanticCrateGroupSize = groupSize });

        var results = await analyzer.AnalyzeProjectAsync(toolchain.ManifestPath, Context(toolchain));

        var nodes = results.SelectMany(result => result.Nodes).ToList();
        var byQualifiedName = nodes.GroupBy(node => node.QualifiedName)
            .ToDictionary(group => group.Key, group => group.First());
        string Describe(string qualifiedName) =>
            byQualifiedName.TryGetValue(qualifiedName, out var node) && node.Properties.ContainsKey("scip_symbol")
                ? (string)node.Properties["scip_symbol"]
                : byQualifiedName.TryGetValue(qualifiedName, out node)
                    ? $"{node.Name}@{node.FilePath}"
                    : qualifiedName;
        return (
            nodes.Select(node => $"{node.QualifiedName}|{node.Name}|{node.FilePath}|{node.Label}")
                .Order(StringComparer.Ordinal).ToList(),
            results.SelectMany(result => result.Edges)
                .Select(edge => $"{edge.Type} {Describe(edge.SourceQN)} -> {Describe(edge.TargetQN)}")
                .Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// A crate document whose function defines a nested fn as SCIP <c>local 0</c>, calls it, reads
    /// a variable (<c>local 1</c>) and optionally calls another crate's function.
    /// </summary>
    private static string LocalsScipDocument(string crate, string function, string? calledSymbol = null)
    {
        var symbol = FakeRustToolchain.Symbol(crate, function);
        var occurrences = new List<object>
        {
            new Dictionary<string, object>
            {
                ["symbol"] = symbol, ["symbolRoles"] = 1,
                ["range"] = new[] { 0, 7, 7 + function.Length }, ["enclosingRange"] = new[] { 0, 0, 9, 1 }
            },
            new Dictionary<string, object>
            {
                ["symbol"] = "local 0", ["symbolRoles"] = 1,
                ["range"] = new[] { 1, 7, 12 }, ["enclosingRange"] = new[] { 1, 4, 3, 5 }
            },
            new Dictionary<string, object> { ["symbol"] = "local 0", ["symbolRoles"] = 8, ["range"] = new[] { 5, 4, 9 } },
            new Dictionary<string, object> { ["symbol"] = "local 1", ["symbolRoles"] = 8, ["range"] = new[] { 6, 4, 9 } }
        };
        if (calledSymbol is not null)
        {
            occurrences.Add(new Dictionary<string, object>
            {
                ["symbol"] = calledSymbol, ["symbolRoles"] = 8, ["range"] = new[] { 7, 4, 16 }
            });
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["language"] = "rust",
            ["relativePath"] = $"crates/{crate}/src/lib.rs",
            ["symbols"] = new object[]
            {
                new Dictionary<string, object> { ["symbol"] = symbol, ["kind"] = "Function", ["displayName"] = function },
                new Dictionary<string, object> { ["symbol"] = "local 0", ["kind"] = "Function", ["displayName"] = $"{crate}_inner" },
                new Dictionary<string, object> { ["symbol"] = "local 1", ["kind"] = "Variable", ["displayName"] = "value" }
            },
            ["occurrences"] = occurrences
        });
    }

    [Fact]
    public async Task AnalyzeWorkspaceAsync_GroupTimeout_FailsOnlyTheSlowGroup()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.SlowCratesVariable, "macros");
        // Deadlines run on a manual clock: the group timeout passes only once the slow group's
        // analyzer is running, so host load cannot time out the fast group or the setup.
        var clock = new ManualTimeProvider();
        var analyzer = CreateAnalyzer(
            new IndexingOptions
            {
                RustSemanticCrateGroupSize = 2,
                // Virtual seconds: far beyond the slow group's 30s sleep, so only the manual clock can fire it.
                RustSemanticGroupTimeoutSeconds = 300
            },
            clock);

        var running = analyzer.AnalyzeWorkspaceAsync(toolchain.ManifestPath, Context(toolchain));
        await toolchain.WaitForSlowAnalyzerAsync();
        clock.Advance(TimeSpan.FromSeconds(300));
        var analysis = await running;

        analysis.Groups.Select(group => string.Join(",", group.Crates)).ShouldBe(["alpha,beta", "macros"]);
        analysis.Groups[0].Failure.ShouldBeNull();
        var failure = analysis.Groups[1].Failure.ShouldBeOfType<RustSemanticIndexingException>();
        failure.FailureCode.ShouldBe("rust_semantic_group_timeout");
        RustSemanticFailureCategory.Classify(failure).ShouldBe(RustSemanticFailureCategory.Timeout);
    }

    [Fact]
    public async Task AnalyzeWorkspaceAsync_WorkspaceTimeout_StopsRunningGroupAndSkipsTheRest()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.SlowCratesVariable, "alpha");
        // The workspace deadline passes on a manual clock once alpha's analyzer is running, so the
        // stage it interrupts does not depend on how fast the host gets through setup.
        var clock = new ManualTimeProvider();
        var analyzer = new RustProjectAnalyzer(
            NullLogger<RustProjectAnalyzer>.Instance,
            TimeSpan.FromSeconds(60),
            groupSettings: new RustCrateGroupSettings(
                GroupSize: 1,
                GroupTimeout: TimeSpan.FromSeconds(60),
                MemoryLimitBytes: 0,
                BuildScripts: false,
                ProcMacros: false,
                // Virtual: far beyond alpha's 30s sleep, so only the manual clock can fire it.
                WorkspaceTimeout: TimeSpan.FromHours(1)),
            timeProvider: clock);

        var running = analyzer.AnalyzeWorkspaceAsync(toolchain.ManifestPath, Context(toolchain));
        await toolchain.WaitForSlowAnalyzerAsync();
        clock.Advance(TimeSpan.FromHours(1));
        var analysis = await running;

        analysis.Groups.Select(group => string.Join(",", group.Crates)).ShouldBe(["alpha", "beta", "macros"]);
        foreach (var group in analysis.Groups)
        {
            group.Results.ShouldBeNull();
            var failure = group.Failure.ShouldBeOfType<RustSemanticIndexingException>();
            failure.FailureCode.ShouldBe("rust_semantic_workspace_timeout");
            RustSemanticFailureCategory.Classify(failure).ShouldBe(RustSemanticFailureCategory.Timeout);
        }

        // The running group is stopped mid-command; groups after the deadline never start.
        analysis.Groups[0].Failure!.Message.ShouldContain("during 'rust-analyzer'");
        analysis.Groups[1].Failure!.Message.ShouldContain("before crates beta were analyzed");
        analysis.Groups[2].Failure!.Message.ShouldContain("before crates macros were analyzed");
        analysis.Groups.Select(group => group.Skipped).ShouldBe([false, true, true]);
        analysis.Groups.Select(group => group.Index).ShouldBe([0, 1, 2]);
        analysis.Groups[0].Duration.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        analysis.Groups.Skip(1).ShouldAllBe(group => group.Duration == TimeSpan.Zero && group.PeakRssBytes == null);
        toolchain.ProjectJsonFiles().Select(FakeRustToolchain.MemberCrates).ShouldBe([["alpha"]]);
    }

    [Fact]
    public async Task AnalyzeWorkspaceAsync_BuildScriptsWithoutProcMacros_PassesBuildOutputsButNoDylib()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        SetBuildOutput(toolchain);
        var analyzer = CreateAnalyzer(new IndexingOptions { RustSemanticBuildScripts = true });

        var analysis = await analyzer.AnalyzeWorkspaceAsync(toolchain.ManifestPath, Context(toolchain));

        analysis.Groups.ShouldHaveSingleItem().Failure.ShouldBeNull();
        var check = (await File.ReadAllLinesAsync(toolchain.CargoLogPath))
            .Single(line => line.StartsWith("check ", StringComparison.Ordinal));
        check.ShouldContain("--package alpha@0.1.0");
        check.ShouldContain("--package beta@0.1.0");
        check.ShouldContain("--package macros@0.1.0");
        using var project = JsonDocument.Parse(toolchain.ProjectJsonFiles().Single());
        var beta = Crate(project, "beta");
        beta.GetProperty("cfg").EnumerateArray().Select(cfg => cfg.GetString()).ShouldContain("has_generated_code");
        beta.GetProperty("env").GetProperty("OUT_DIR").GetString().ShouldBe("/out/beta");
        Crate(project, "macros").TryGetProperty("proc_macro_dylib_path", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AnalyzeWorkspaceAsync_BuildScriptsAndProcMacros_PassesProcMacroDylib()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        SetBuildOutput(toolchain);
        var analyzer = CreateAnalyzer(new IndexingOptions
        {
            RustSemanticBuildScripts = true,
            RustSemanticProcMacros = true
        });

        await analyzer.AnalyzeWorkspaceAsync(toolchain.ManifestPath, Context(toolchain));

        using var project = JsonDocument.Parse(toolchain.ProjectJsonFiles().Single());
        Crate(project, "macros").GetProperty("proc_macro_dylib_path").GetString()
            .ShouldBe("/target/debug/deps/libmacros.so");
    }

    [Fact]
    public void ReadProcessGroupRssBytes_SumsOnlyTheProcessGroupsResidentPages()
    {
        var proc = Path.Combine(Path.GetTempPath(), $"codegraph-fake-proc-{Guid.NewGuid():N}");
        try
        {
            WriteStat(proc, 100, "rust-analyzer", processGroup: 100, rssPages: 10);
            WriteStat(proc, 101, "proc macro srv", processGroup: 100, rssPages: 5);
            WriteStat(proc, 200, "dotnet", processGroup: 200, rssPages: 1000);
            Directory.CreateDirectory(Path.Combine(proc, "self"));

            RustProjectAnalyzer.ReadProcessGroupRssBytes(proc, 100)
                .ShouldBe(15L * Environment.SystemPageSize);
        }
        finally
        {
            Directory.Delete(proc, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeWorkspaceAsync_GroupExceedingMemoryLimit_IsKilledAndReportedAsResourceExhausted()
    {
        // Enforcement samples /proc for the setsid process group: Linux only.
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid"))
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.SlowCratesVariable, "alpha beta macros");
        var analyzer = new RustProjectAnalyzer(
            NullLogger<RustProjectAnalyzer>.Instance,
            TimeSpan.FromSeconds(30),
            groupSettings: new RustCrateGroupSettings(
                GroupSize: 8,
                GroupTimeout: TimeSpan.FromSeconds(30),
                MemoryLimitBytes: 4096,
                BuildScripts: false,
                ProcMacros: false,
                WorkspaceTimeout: TimeSpan.FromSeconds(60)));

        var analysis = await analyzer.AnalyzeWorkspaceAsync(toolchain.ManifestPath, Context(toolchain));

        var failure = analysis.Groups.ShouldHaveSingleItem().Failure.ShouldBeOfType<RustSemanticIndexingException>();
        failure.FailureCode.ShouldBe("rust_semantic_memory_limit_exceeded");
        RustSemanticFailureCategory.Classify(failure).ShouldBe(RustSemanticFailureCategory.ResourceExhausted);
    }

    private static void WriteStat(string proc, int pid, string command, int processGroup, long rssPages)
    {
        var directory = Path.Combine(proc, pid.ToString());
        Directory.CreateDirectory(directory);
        // Fields 3..24: state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt
        // utime stime cutime cstime priority nice threads itrealvalue starttime vsize rss
        File.WriteAllText(Path.Combine(directory, "stat"),
            $"{pid} ({command}) S 1 {processGroup} {processGroup} 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 0 1000 {rssPages} 0\n");
    }

    private static bool HasProperty(JsonElement element, string name) => element.TryGetProperty(name, out _);

    private static void SetBuildOutput(FakeRustToolchain toolchain) =>
        toolchain.SetCheckOutput(
            new Dictionary<string, object>
            {
                ["reason"] = "build-script-executed",
                ["package_id"] = toolchain.PackageId("beta"),
                ["cfgs"] = new[] { "has_generated_code" },
                ["env"] = new[] { new[] { "GENERATED", "1" } },
                ["out_dir"] = "/out/beta"
            },
            new Dictionary<string, object>
            {
                ["reason"] = "compiler-artifact",
                ["package_id"] = toolchain.PackageId("macros"),
                ["target"] = new Dictionary<string, object> { ["kind"] = new[] { "proc-macro" } },
                ["filenames"] = new[] { "/target/debug/deps/libmacros.so" }
            });

    private static JsonElement Crate(JsonDocument project, string name) =>
        project.RootElement.GetProperty("crates").EnumerateArray()
            .Single(crate => crate.GetProperty("display_name").GetString() == name);

    private static FakeRustToolchain CreateThreeCrateWorkspace()
    {
        var toolchain = new FakeRustToolchain()
            .AddCrate(
                "alpha",
                "pub fn alpha_entry() -> u32 {\n    beta::beta_compute(2)\n}\n",
                FakeRustToolchain.ScipDocument(
                    "alpha", "alpha_entry", FakeRustToolchain.Symbol("beta", "beta_compute")),
                dependencies: ["beta"])
            .AddCrate(
                "beta",
                "pub fn beta_compute(x: u32) -> u32 {\n    x * 2\n}\n",
                FakeRustToolchain.ScipDocument("beta", "beta_compute"))
            .AddCrate(
                "macros",
                "pub fn macros_helper() -> u32 {\n    7\n}\n",
                FakeRustToolchain.ScipDocument("macros", "macros_helper"),
                procMacro: true);
        toolchain.Install();
        return toolchain;
    }

    private static RustProjectAnalyzer CreateAnalyzer(IndexingOptions options) =>
        new(NullLogger<RustProjectAnalyzer>.Instance, Options.Create(options));

    private static RustProjectAnalyzer CreateAnalyzer(IndexingOptions options, TimeProvider timeProvider) =>
        new(
            NullLogger<RustProjectAnalyzer>.Instance,
            TimeSpan.FromSeconds(options.RustSemanticCommandTimeoutSeconds),
            options.RustSemanticStderrTailCharacters,
            options.RustSemanticMaxThreads,
            groupSettings: RustCrateGroupSettings.From(options),
            timeProvider: timeProvider);

    private static ExtractorContext Context(FakeRustToolchain toolchain) => new()
    {
        ProjectName = "RustGroups",
        RootPath = toolchain.RootPath
    };
}
