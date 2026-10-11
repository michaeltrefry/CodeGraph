using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Extractors.Rust;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Metadata;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Data;
using CodeGraph.Tests.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Services;

[Collection("Process environment")]
public sealed class RustCrateGroupPipelineTests
{
    [Fact]
    public async Task IndexProjectAsync_ThreeCrateGroups_FailingGroupFallsBackAloneAndCrossGroupCallLinks()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "gamma");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics);

        await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        // Only the failing group falls back, with exactly its crate.
        var fallback = diagnostics.RustSemanticFallbacks.ShouldHaveSingleItem();
        fallback.Crates.ShouldBe(["gamma"]);
        fallback.Manifest.ShouldBe("Cargo.toml");
        fallback.DetailCode.ShouldBe("rust_analyzer_scip_failed");
        var gammaNodes = CodeNodes(store, "crates/gamma/src/lib.rs");
        gammaNodes.ShouldContain(node => node.Name == "gamma_value");
        gammaNodes.ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        gammaNodes.ShouldAllBe(node =>
            (string)node.Properties[GraphNodeConfidence.ExtractionSourceProperty] ==
            GraphNodeConfidence.TreeSitterFallbackSource);

        // The other two groups stay semantic.
        foreach (var (crate, function) in new[] { ("alpha", "alpha_entry"), ("beta", "beta_compute") })
        {
            var nodes = CodeNodes(store, $"crates/{crate}/src/lib.rs");
            nodes.ShouldContain(node => node.Name == function && (string)node.Properties["source"] == "scip");
            nodes.ShouldNotContain(node => GraphNodeConfidence.IsLowConfidence(node));
        }

        // alpha (group 0) calls beta (group 1): the CALLS edge joins the two definitions.
        var caller = store.Nodes.Single(node => node.Name == "alpha_entry");
        var target = store.Nodes.Single(node =>
            node.Name == "beta_compute" && node.FilePath == "crates/beta/src/lib.rs");
        store.Edges.ShouldContain(edge =>
            edge.Type == EdgeType.CALLS && edge.SourceId == caller.Id && edge.TargetId == target.Id);
        store.Nodes.ShouldNotContain(node =>
            node.Label == NodeLabel.ExternalSymbol &&
            node.Properties.ContainsKey("scip_symbol") &&
            (string)node.Properties["scip_symbol"] == FakeRustToolchain.Symbol("beta", "beta_compute"));

        var repository = await store.GetRepositoryByName("RustGroups");
        var recorded = ((JsonElement)repository!.Properties!["rustSemanticFallbacks"]).EnumerateArray().Single();
        recorded.GetProperty("crates").EnumerateArray().Select(crate => crate.GetString()).ShouldBe(["gamma"]);

        // Every group has an outcome; only gamma's is a fallback.
        var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
        outcomes.Groups.Select(group => group.Outcome).ShouldBe(["semantic", "semantic", "fallback"]);
        (outcomes.SemanticGroups, outcomes.FallbackGroups, outcomes.SkippedGroups).ShouldBe((2, 1, 0));
        var gamma = outcomes.Groups[2];
        gamma.Crates.ShouldBe(["gamma"]);
        gamma.FailureCode.ShouldBe(fallback.FailureCode);
        gamma.DetailCode.ShouldBe("rust_analyzer_scip_failed");
        gamma.DurationMs.ShouldBeGreaterThan(0);
        gamma.Documents.ShouldBeNull();
        outcomes.Groups[0].FailureCode.ShouldBeNull();
        outcomes.Groups[0].Documents.ShouldBe(1);
        var persisted = (JsonElement)repository.Properties["rustSemanticOutcomes"];
        persisted.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("outcome").GetString())
            .ShouldBe(["semantic", "semantic", "fallback"]);
    }

    [Fact]
    public async Task IndexProjectAsync_AllGroupsSemantic_RecordsEveryGroupOutcomeAndSettingsSnapshot()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics);

        await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        diagnostics.RustSemanticFallbacks.ShouldBeEmpty();
        var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
        outcomes.Manifest.ShouldBe("Cargo.toml");
        (outcomes.CrateCount, outcomes.GroupCount, outcomes.GroupSize).ShouldBe((3, 3, 1));
        (outcomes.SemanticGroups, outcomes.FallbackGroups, outcomes.SkippedGroups).ShouldBe((3, 0, 0));
        outcomes.TotalDurationMs.ShouldBeGreaterThan(0);
        outcomes.Settings.ShouldBe(new RustSemanticSettingsSnapshot(
            GroupSize: 1,
            GroupTimeoutSeconds: 600,
            WorkspaceTimeoutSeconds: 3600,
            GroupMemoryLimitMb: 8192,
            BuildScripts: false,
            ProcMacros: false));
        outcomes.Groups.Select(group => group.Index).ShouldBe([0, 1, 2]);
        outcomes.Groups.Select(group => string.Join(",", group.Crates)).ShouldBe(["alpha", "beta", "gamma"]);
        foreach (var group in outcomes.Groups)
        {
            group.Outcome.ShouldBe(RustSemanticGroupOutcome.Semantic);
            group.FailureCode.ShouldBeNull();
            group.DetailCode.ShouldBeNull();
            group.DurationMs.ShouldBeGreaterThan(0);
            group.Documents.ShouldBe(1);
            group.Nodes.ShouldNotBeNull().ShouldBeGreaterThan(0);
            group.Edges.ShouldNotBeNull();
            // Peak RSS comes from the /proc sampling of the setsid process group (Linux only).
            if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/setsid"))
                group.PeakRssMb.ShouldNotBeNull();
            else
                group.PeakRssMb.ShouldBeNull();
        }

        outcomes.Summary().ShouldBe(
            $"Rust semantic: 3 groups (3 semantic, 0 fallback, 0 skipped), 3 crates, " +
            $"{Math.Round(outcomes.TotalDurationMs / 1000.0)}s.");

        var repository = await store.GetRepositoryByName("RustGroups");
        repository!.Properties!.ShouldNotContainKey("rustSemanticFallbacks");
        var persisted = (JsonElement)repository.Properties["rustSemanticOutcomes"];
        persisted.GetProperty("semanticGroups").GetInt32().ShouldBe(3);
        persisted.GetProperty("settings").GetProperty("groupMemoryLimitMb").GetInt32().ShouldBe(8192);
        var groups = persisted.GetProperty("groups").EnumerateArray().ToList();
        groups.Select(group => group.GetProperty("crates").EnumerateArray().Single().GetString())
            .ShouldBe(["alpha", "beta", "gamma"]);
        groups.ShouldAllBe(group => group.GetProperty("durationMs").GetInt64() > 0);
    }

    [Fact]
    public async Task IndexProjectAsync_MultiCrateGroups_OutcomesListEachGroupsCrates()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(new InMemoryGraphStore(), diagnostics, groupSize: 2);

        await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
        (outcomes.CrateCount, outcomes.GroupCount, outcomes.GroupSize).ShouldBe((3, 2, 2));
        outcomes.Groups.Select(group => string.Join(",", group.Crates)).ShouldBe(["alpha,beta", "gamma"]);
        outcomes.Groups.Select(group => group.Documents).ShouldBe([2, 1]);
    }

    [Fact]
    public async Task IndexProjectAsync_SeveralFailingGroups_RecordsOneFallbackPerGroupInGroupOrder()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "gamma alpha");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics);

        await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        diagnostics.RustSemanticFallbacks.Select(fallback => string.Join(",", fallback.Crates))
            .ShouldBe(["alpha", "gamma"]);
        CodeNodes(store, "crates/alpha/src/lib.rs").ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        CodeNodes(store, "crates/gamma/src/lib.rs").ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        CodeNodes(store, "crates/beta/src/lib.rs").ShouldNotContain(node => GraphNodeConfidence.IsLowConfidence(node));
    }

    [Fact]
    public async Task IndexProjectAsync_WorkspaceTimeout_EveryUnfinishedGroupFallsBackAndTheRunCompletes()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.SlowCratesVariable, "alpha");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics, workspaceTimeoutSeconds: 1);

        await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        diagnostics.RustSemanticFallbacks.Select(fallback => string.Join(",", fallback.Crates))
            .ShouldBe(["alpha", "beta", "gamma"]);
        diagnostics.RustSemanticFallbacks.ShouldAllBe(fallback =>
            fallback.FailureCode == RustSemanticFailureCategory.Timeout &&
            fallback.DetailCode == "rust_semantic_workspace_timeout");
        foreach (var crate in new[] { "alpha", "beta", "gamma" })
        {
            var nodes = CodeNodes(store, $"crates/{crate}/src/lib.rs");
            nodes.ShouldNotBeEmpty();
            nodes.ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        }

        // alpha ran and was stopped; beta and gamma never started.
        var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
        outcomes.Groups.Select(group => group.Outcome).ShouldBe(["fallback", "skipped", "skipped"]);
        (outcomes.SemanticGroups, outcomes.FallbackGroups, outcomes.SkippedGroups).ShouldBe((0, 1, 2));
        outcomes.Settings.WorkspaceTimeoutSeconds.ShouldBe(1);
        outcomes.Groups[0].DurationMs.ShouldBeGreaterThan(0);
        outcomes.Groups.Skip(1).ShouldAllBe(group =>
            group.DurationMs == 0 &&
            group.PeakRssMb == null &&
            group.FailureCode == RustSemanticFailureCategory.Timeout &&
            group.DetailCode == "rust_semantic_workspace_timeout");
        outcomes.Groups.Select(group => string.Join(",", group.Crates)).ShouldBe(["alpha", "beta", "gamma"]);
    }

    [Fact]
    public async Task IndexProjectAsync_MariaDb_PersistsRustSemanticOutcomesReadableFromProjectList()
    {
        if (OperatingSystem.IsWindows())
            return;

        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"codegraph_rust_outcomes_test_{Guid.NewGuid():N}"
        };
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "beta");
        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;
            await using var context = new CodeGraphDbContext(dbOptions);
            var store = new MySqlGraphStore(
                context,
                storageOptions,
                NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context),
                new MySqlMetricsStore(context),
                new MySqlReviewStore(context),
                runner);
            var pipeline = CreatePipeline(store, new IndexingRunDiagnostics());

            await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

            // GET /api/projects?search=RustGroups serializes this response with the web defaults.
            var list = await new ProjectQueryService(store, Options.Create(new RepositorySourceOptions()))
                .ListAsync("RustGroups", null, 1, 25);
            using var response = JsonDocument.Parse(
                JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var properties = response.RootElement.GetProperty("items").EnumerateArray().Single()
                .GetProperty("properties");
            var outcomes = properties.GetProperty("rustSemanticOutcomes");
            outcomes.GetProperty("groupCount").GetInt32().ShouldBe(3);
            outcomes.GetProperty("fallbackGroups").GetInt32().ShouldBe(1);
            outcomes.GetProperty("settings").GetProperty("groupSize").GetInt32().ShouldBe(1);
            var groups = outcomes.GetProperty("groups").EnumerateArray().ToList();
            groups.Select(group => group.GetProperty("outcome").GetString())
                .ShouldBe(["semantic", "fallback", "semantic"]);
            groups[1].GetProperty("crates").EnumerateArray().Select(crate => crate.GetString()).ShouldBe(["beta"]);
            groups[1].GetProperty("detailCode").GetString().ShouldBe("rust_analyzer_scip_failed");
            groups.ShouldAllBe(group => group.GetProperty("durationMs").GetInt64() > 0);
            // The fallback record is unchanged alongside the outcomes.
            properties.GetProperty("rustSemanticFallbacks").EnumerateArray().Single()
                .GetProperty("crates").EnumerateArray().Single().GetString().ShouldBe("beta");
        }
        finally
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS `{builder.Database}`";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static List<GraphNode> CodeNodes(InMemoryGraphStore store, string filePath) =>
        store.Nodes
            .Where(node => node.Project == "RustGroups" && node.FilePath == filePath &&
                           node.Label is not (NodeLabel.File or NodeLabel.Folder))
            .ToList();

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
                "gamma",
                "pub fn gamma_value() -> u32 {\n    7\n}\n",
                FakeRustToolchain.ScipDocument("gamma", "gamma_value"));
        toolchain.Install();
        return toolchain;
    }

    private static IndexingPipeline CreatePipeline(
        IGraphStore store,
        IndexingRunDiagnostics diagnostics,
        int workspaceTimeoutSeconds = 3600,
        int groupSize = 1)
    {
        var options = Options.Create(new IndexingOptions
        {
            MaxParallelFiles = 1,
            RustSemanticCrateGroupSize = groupSize,
            RustSemanticWorkspaceTimeoutSeconds = workspaceTimeoutSeconds
        });
        return new IndexingPipeline(
            store,
            [new CodeGraph.Extractors.TreeSitter.TreeSitterExtractor(
                NullLogger<CodeGraph.Extractors.TreeSitter.TreeSitterExtractor>.Instance)],
            options,
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            rustAnalyzer: new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance, options),
            cargoManifestExtractor: new CargoManifestExtractor(),
            diagnostics: diagnostics);
    }
}
