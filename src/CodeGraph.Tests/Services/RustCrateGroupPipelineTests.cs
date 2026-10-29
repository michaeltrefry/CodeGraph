using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Extractors.Rust;
using CodeGraph.Models;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Messaging;
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
    public async Task IndexRepositoryAsync_ThreeCrateGroups_FailingGroupFallsBackAloneAndCrossGroupCallLinks()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "gamma");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics);

        await pipeline.IndexRepositoryAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

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
    public async Task IndexRepositoryAsync_AllGroupsSemantic_RecordsEveryGroupOutcomeAndSettingsSnapshot()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics);

        await pipeline.IndexRepositoryAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

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
    public async Task IndexRepositoryAsync_MultiCrateGroups_OutcomesListEachGroupsCrates()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(new InMemoryGraphStore(), diagnostics, groupSize: 2);

        await pipeline.IndexRepositoryAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
        (outcomes.CrateCount, outcomes.GroupCount, outcomes.GroupSize).ShouldBe((3, 2, 2));
        outcomes.Groups.Select(group => string.Join(",", group.Crates)).ShouldBe(["alpha,beta", "gamma"]);
        outcomes.Groups.Select(group => group.Documents).ShouldBe([2, 1]);
    }

    [Fact]
    public async Task IndexRepositoryAsync_SeveralFailingGroups_RecordsOneFallbackPerGroupInGroupOrder()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "gamma alpha");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics);

        await pipeline.IndexRepositoryAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        diagnostics.RustSemanticFallbacks.Select(fallback => string.Join(",", fallback.Crates))
            .ShouldBe(["alpha", "gamma"]);
        CodeNodes(store, "crates/alpha/src/lib.rs").ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        CodeNodes(store, "crates/gamma/src/lib.rs").ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        CodeNodes(store, "crates/beta/src/lib.rs").ShouldNotContain(node => GraphNodeConfidence.IsLowConfidence(node));
    }

    [Fact]
    public async Task IndexRepositoryAsync_WorkspaceTimeout_EveryUnfinishedGroupFallsBackAndTheRunCompletes()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.SlowCratesVariable, "alpha");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics, workspaceTimeoutSeconds: 1);

        await pipeline.IndexRepositoryAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

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
    public async Task IndexRepositoryAsync_MariaDb_PersistsRustSemanticOutcomesReadableFromProjectList()
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

            await pipeline.IndexRepositoryAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

            // GET /api/repositories?search=RustGroups serializes this response with the web defaults.
            var list = await new RepositoryQueryService(store, Options.Create(new RepositorySourceOptions()))
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
            // Release pooled sessions (idle ones linger ~180s per database) before dropping.
            await MySqlConnection.ClearAllPoolsAsync();
            drop.CommandText = $"DROP DATABASE IF EXISTS `{builder.Database}`";
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ProcessRepositoryThenReAnalyze_MariaDb_RustRecordsReplaceTheLastRunsAlongsideLanguageStats()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var database = await MariaDbRustDatabase.CreateAsync();
        using var toolchain = CreateThreeCrateWorkspace();

        // A row written before Rust outcomes existed: only the language-stats keys.
        await database.UseStoreAsync(store => store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "RustGroups",
            Language = "Rust",
            Framework = "Cargo",
            Properties = """{"languageStats":{"Rust":{"files":1}},"primaryLanguageBasis":"locNonBlank"}"""
        }));

        // Consumer-driven run (POST /api/indexer/repositories/process → ProcessRepository).
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "beta");
        await database.UseStoreAsync(store => CreateRepositoryService(store, toolchain.RootPath)
            .ProcessRepository(new ProcessRepository
            {
                Name = "RustGroups",
                ShouldIndex = true,
                ShouldAnalyze = false,
                SkipIfUpToDate = false
            }, CancellationToken.None));

        var afterProcess = await database.UseStoreAsync(ListedPropertiesAsync);
        afterProcess.GetProperty("languageStats").GetProperty("Rust").GetProperty("files").GetInt32().ShouldBe(3);
        afterProcess.GetProperty("primaryLanguageBasis").GetString().ShouldBe("locNonBlank");
        var processOutcomes = afterProcess.GetProperty("rustSemanticOutcomes");
        (processOutcomes.GetProperty("semanticGroups").GetInt32(), processOutcomes.GetProperty("fallbackGroups").GetInt32())
            .ShouldBe((2, 1));
        afterProcess.GetProperty("rustSemanticFallbacks").EnumerateArray().Single()
            .GetProperty("crates").EnumerateArray().Single().GetString().ShouldBe("beta");

        // Durable ReAnalyze run (replacement path) without fallbacks: outcomes are replaced
        // and the fallback record of the previous run is gone.
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, null);
        await database.UseStoreAsync(store => CreateRepositoryService(store, toolchain.RootPath)
            .ReAnalyzeRepository("RustGroups", CancellationToken.None));

        var afterReAnalyze = await database.UseStoreAsync(ListedPropertiesAsync);
        afterReAnalyze.GetProperty("languageStats").GetProperty("Rust").GetProperty("files").GetInt32().ShouldBe(3);
        var reAnalyzeOutcomes = afterReAnalyze.GetProperty("rustSemanticOutcomes");
        (reAnalyzeOutcomes.GetProperty("semanticGroups").GetInt32(), reAnalyzeOutcomes.GetProperty("fallbackGroups").GetInt32())
            .ShouldBe((3, 0));
        afterReAnalyze.TryGetProperty("rustSemanticFallbacks", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task IndexRepositoryAsync_MariaDb_RunFailingAfterRustAnalysisKeepsTheLastCompletedIndexResults()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var database = await MariaDbRustDatabase.CreateAsync();
        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "beta");
        await database.UseStoreAsync(store => CreatePipeline(store, new IndexingRunDiagnostics())
            .IndexRepositoryAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None));
        var completed = await database.UseStoreAsync(store => store.GetRepositoryByName("RustGroups"));

        // The next run analyzes the workspace (no fallback this time) but its graph write fails.
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, null);
        var failure = await Should.ThrowAsync<InvalidOperationException>(database.UseStoreAsync(store =>
            CreatePipeline(FailingFileReplacementStore.Wrap(store), new IndexingRunDiagnostics())
                .IndexRepositoryAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None)));
        failure.Message.ShouldBe(FailingFileReplacementStore.FailureMessage);

        // The repository still describes the last completed run: same indexedAt, same records.
        var afterFailure = await database.UseStoreAsync(store => store.GetRepositoryByName("RustGroups"));
        afterFailure!.IndexedAt.ShouldBe(completed!.IndexedAt);
        var properties = await database.UseStoreAsync(ListedPropertiesAsync);
        properties.GetProperty("rustSemanticOutcomes").GetProperty("fallbackGroups").GetInt32().ShouldBe(1);
        properties.GetProperty("rustSemanticFallbacks").EnumerateArray().Single()
            .GetProperty("crates").EnumerateArray().Single().GetString().ShouldBe("beta");
    }

    /// <summary>The repository's properties as GET /api/repositories?search= serializes them.</summary>
    private static async Task<JsonElement> ListedPropertiesAsync(IGraphStore store)
    {
        var list = await new RepositoryQueryService(store, Options.Create(new RepositorySourceOptions()))
            .ListAsync("RustGroups", null, 1, 25);
        using var response = JsonDocument.Parse(
            JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return response.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("properties").Clone();
    }

    private static RepositoryService CreateRepositoryService(IGraphStore store, string rootPath) =>
        new(
            store,
            new NoOpBatchAnalysisService(),
            new NoOpMessageBus(),
            new FixedRepoProvider(rootPath),
            CreatePipeline(store, new IndexingRunDiagnostics()),
            Options.Create(new IndexingOptions { MaxParallelRepos = 1 }),
            NullLogger<RepositoryService>.Instance);

    /// <summary>A throwaway MariaDB database; each operation gets its own DbContext, like a request scope.</summary>
    private sealed class MariaDbRustDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly MySqlConnectionStringBuilder _builder;
        private readonly IOptions<MariaDbStorageOptions> _storageOptions;
        private readonly MariaDbMigrationRunner _runner;

        private MariaDbRustDatabase(string serverConnectionString)
        {
            _serverConnectionString = serverConnectionString;
            _builder = new MySqlConnectionStringBuilder(serverConnectionString)
            {
                Database = $"codegraph_rust_persist_test_{Guid.NewGuid():N}"
            };
            _storageOptions = Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = _builder.ConnectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            });
            _runner = new MariaDbMigrationRunner(_storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        }

        public static async Task<MariaDbRustDatabase> CreateAsync()
        {
            var database = new MariaDbRustDatabase(MariaDbTestEnvironment.RequireConnectionString());
            await database._runner.ApplyConfiguredMigrationsAsync();
            return database;
        }

        public Task UseStoreAsync(Func<IGraphStore, Task> action) =>
            UseStoreAsync(async store =>
            {
                await action(store);
                return true;
            });

        public async Task<T> UseStoreAsync<T>(Func<IGraphStore, Task<T>> action)
        {
            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(_builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;
            await using var context = new CodeGraphDbContext(dbOptions);
            return await action(new MySqlGraphStore(
                context,
                _storageOptions,
                NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context),
                new MySqlMetricsStore(context),
                new MySqlReviewStore(context),
                _runner));
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new MySqlConnection(_serverConnectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            // Release pooled sessions (idle ones linger ~180s per database) before dropping.
            await MySqlConnection.ClearAllPoolsAsync();
            drop.CommandText = $"DROP DATABASE IF EXISTS `{_builder.Database}`";
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Delegates to a real store but fails the incremental graph write.</summary>
    public class FailingFileReplacementStore : DispatchProxy
    {
        public const string FailureMessage = "Simulated graph write failure.";
        private IGraphStore _inner = null!;

        public static IGraphStore Wrap(IGraphStore inner)
        {
            var proxy = Create<IGraphStore, FailingFileReplacementStore>();
            ((FailingFileReplacementStore)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IGraphStore.ReplaceRepositoryFilesAsync))
                throw new InvalidOperationException(FailureMessage);
            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
        }
    }

    private sealed class FixedRepoProvider(string rootPath) : IRepoProvider
    {
        public Task<ResolvedRepository> ResolveRepositoryAsync(
            string repoName,
            string? localPath,
            string? repoUrl,
            CancellationToken ct = default) =>
            Task.FromResult(new ResolvedRepository(rootPath, $"folder:{repoName}", rootPath, null));

        public Task<string> EnsureLocalAsync(
            string repoName,
            string? localPath,
            string? repoUrl,
            CancellationToken ct = default) => Task.FromResult(rootPath);

        public Task<List<DiscoveredRepository>> DiscoverRepositoriesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<DiscoveredRepository>());

        public Task<List<DiscoveredRepository>> SearchRepositoriesAsync(
            string searchTerm,
            CancellationToken ct = default) =>
            Task.FromResult(new List<DiscoveredRepository>());
    }

    private sealed class NoOpMessageBus : IMessageBus
    {
        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class =>
            Task.CompletedTask;
    }

    private sealed class NoOpBatchAnalysisService : IBatchAnalysisService
    {
        public Task SubmitAnalysisBatchAsync(
            string repoName,
            string? repoPath = null,
            bool includeAllSource = false,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ProcessCompletedBatchAsync(
            string repoName,
            string providerBatchId,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task SynthesizeRepoSummaryAsync(
            string repoName,
            string batchId,
            CancellationToken ct) => Task.CompletedTask;

        public Task WriteCodeGraphDocsAsync(string repoName, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static List<GraphNode> CodeNodes(InMemoryGraphStore store, string filePath) =>
        store.Nodes
            .Where(node => node.Repository == "RustGroups" && node.FilePath == filePath &&
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
