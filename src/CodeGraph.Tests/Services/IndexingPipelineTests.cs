using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Metadata;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class IndexingPipelineTests
{
    [Fact]
    public async Task IndexProjectAsync_UsesDominantLanguageMetadata_InsteadOfFirstCompletedFile()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-language-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "build.sh"), "#!/usr/bin/env bash\necho hi\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "main.c"), "int main(void) { return 0; }\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "main.h"), "#pragma once\n");

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TestMetadataExtractor()],
                Options.Create(new IndexingOptions { MaxParallelFiles = 3 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("DryBox", rootPath, ct: CancellationToken.None);

            var repo = await store.GetRepositoryByName("DryBox");
            repo.ShouldNotBeNull();
            repo.Language.ShouldBe("C");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_UsesNonBlankLocForPrimaryLanguage_AndPersistsLanguageStats()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-language-loc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "a.py"), "print('a')\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "b.py"), "print('b')\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "c.py"), "print('c')\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "lib.rs"), string.Join('\n',
                Enumerable.Range(1, 12).Select(i => $"pub fn rust_fn_{i}() {{}}")));

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new MixedRustPythonMetadataExtractor()],
                Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("MixedRepo", rootPath, ct: CancellationToken.None);

            var repo = await store.GetRepositoryByName("MixedRepo");
            repo.ShouldNotBeNull();
            repo.Language.ShouldBe("Rust");
            repo.Properties.ShouldNotBeNull();
            repo.Properties.ShouldContainKey("languageStats");

            var languageStats = (JsonElement)repo.Properties["languageStats"];
            languageStats.TryGetProperty("Rust", out var rustStats).ShouldBeTrue();
            languageStats.TryGetProperty("Python", out var pythonStats).ShouldBeTrue();
            rustStats.GetProperty("locNonBlank").GetInt32().ShouldBe(12);
            pythonStats.GetProperty("files").GetInt32().ShouldBe(3);
            rustStats.GetProperty("locShare").GetDouble().ShouldBeGreaterThan(0.75);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_PersistsDotnetSupportMetadata_InRepositoryProperties()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-support-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "legacy.cs"), "public class Legacy {}");

            var support = new DotnetSupportInfo(
                "out_of_support",
                "Pinned SDK 2.1.802 is out of support.",
                new DotnetSdkSupportInfo("2.1.802", "2.1", ".NET SDK 2.1", "out_of_support", new DateTime(2021, 8, 21, 0, 0, 0, DateTimeKind.Utc), true),
                [new DotnetTargetFrameworkSupportInfo("netcoreapp2.1", ".NET Core 2.1", "out_of_support", new DateTime(2021, 8, 21, 0, 0, 0, DateTimeKind.Utc))]);

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TestSupportMetadataExtractor(support)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("LegacyRepo", rootPath, ct: CancellationToken.None);

            var repo = await store.GetRepositoryByName("LegacyRepo");
            repo.ShouldNotBeNull();
            var storedSupport = DotnetSupportInspector.TryReadStoredSupport(repo.Properties);
            storedSupport.ShouldNotBeNull();
            storedSupport.Sdk.ShouldNotBeNull();
            storedSupport.Sdk.Version.ShouldBe("2.1.802");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_UsesSlnxForSolutionLevelAnalysis()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-slnx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var solutionPath = Path.Combine(rootPath, "Demo.slnx");
            await File.WriteAllTextAsync(solutionPath, "<Solution />");

            var store = new InMemoryGraphStore();
            var solutionAnalyzer = new RecordingSolutionAnalyzer();
            var logger = new RecordingLogger<IndexingPipeline>();
            var pipeline = new IndexingPipeline(
                store,
                [],
                Options.Create(new IndexingOptions { TrustedDotnetRepositories = "folder:Demo" }),
                new LocalFileSystem(),
                logger,
                solutionAnalyzer);

            await pipeline.IndexProjectAsync(
                "Demo", rootPath, repositoryToolingIdentity: "folder:Demo", ct: CancellationToken.None);

            solutionAnalyzer.CalledSolutionPath.ShouldBe(solutionPath);
            solutionAnalyzer.ObservedTrust.ShouldBe(RepositoryToolingTrust.Trusted);
            logger.Messages.ShouldContain(message =>
                message.Contains("SECURITY-AUDIT", StringComparison.Ordinal)
                && message.Contains("folder:Demo", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_AnalyzesAllSolutionsDeterministically_AndDeduplicatesSharedDocuments()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-multi-solution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var zetaSolution = Path.Combine(rootPath, "Zeta.slnx");
            var alphaSolution = Path.Combine(rootPath, "Alpha.sln");
            await File.WriteAllTextAsync(zetaSolution, "<Solution />");
            await File.WriteAllTextAsync(alphaSolution, string.Empty);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Shared.cs"), "public sealed class Shared {}");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "AlphaOnly.cs"), "public sealed class AlphaOnly {}");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "ZetaOnly.cs"), "public sealed class ZetaOnly {}");

            var fallback = new TrackingCSharpExtractor();
            var solutionAnalyzer = new RecordingSolutionAnalyzer((solutionPath, context) =>
                Path.GetFileName(solutionPath) == "Alpha.sln"
                    ? new SolutionAnalysisResult
                    {
                        Documents =
                        [
                            CreateSolutionDocument(context, "Shared.cs", "Shared", includeSelfEdge: true),
                            CreateSolutionDocument(context, "AlphaOnly.cs", "AlphaOnly")
                        ]
                    }
                    : new SolutionAnalysisResult
                    {
                        Documents =
                        [
                            CreateSolutionDocument(context, "Shared.cs", "Shared", includeSelfEdge: true),
                            CreateSolutionDocument(context, "ZetaOnly.cs", "ZetaOnly")
                        ]
                    });
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [fallback],
                Options.Create(new IndexingOptions { TrustedDotnetRepositories = "folder:Multi" }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                solutionAnalyzer);

            await pipeline.IndexProjectAsync(
                "Multi", rootPath, repositoryToolingIdentity: "folder:Multi", ct: CancellationToken.None);

            solutionAnalyzer.CalledSolutionPaths.ShouldBe([alphaSolution, zetaSolution]);
            store.Nodes.Where(node => node.Label == NodeLabel.Class)
                .Select(node => node.Name)
                .OrderBy(name => name)
                .ShouldBe(["AlphaOnly", "Shared", "ZetaOnly"]);
            store.Edges.Count(edge => edge.Type == EdgeType.CALLS).ShouldBe(1);
            fallback.CalledFiles.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_FailedSolutionFallsBackOnlyForUncoveredCSharpFiles()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-multi-solution-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var goodSolution = Path.Combine(rootPath, "AGood.sln");
            var brokenSolution = Path.Combine(rootPath, "BBroken.slnx");
            await File.WriteAllTextAsync(goodSolution, string.Empty);
            await File.WriteAllTextAsync(brokenSolution, "<Solution />");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Good.cs"), "public sealed class Good {}");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Broken.cs"), "public sealed class Broken {}");

            var fallback = new TrackingCSharpExtractor();
            var solutionAnalyzer = new RecordingSolutionAnalyzer((solutionPath, context) =>
            {
                if (Path.GetFileName(solutionPath) == "BBroken.slnx")
                    throw new InvalidOperationException("synthetic solution load failure");

                return new SolutionAnalysisResult
                {
                    Documents = [CreateSolutionDocument(context, "Good.cs", "Good")]
                };
            });
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [fallback],
                Options.Create(new IndexingOptions { TrustedDotnetRepositories = "folder:Fallback" }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                solutionAnalyzer);

            await pipeline.IndexProjectAsync(
                "Fallback", rootPath, repositoryToolingIdentity: "folder:Fallback", ct: CancellationToken.None);

            solutionAnalyzer.CalledSolutionPaths.ShouldBe([goodSolution, brokenSolution]);
            fallback.CalledFiles.ShouldBe([Path.Combine(rootPath, "Broken.cs")]);
            store.Nodes.Where(node => node.Label == NodeLabel.Class)
                .Select(node => node.Name)
                .OrderBy(name => name)
                .ShouldBe(["Broken", "Good"]);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_DefaultPolicy_SkipsSolutionToolingAndUsesSyntaxFallback()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-untrusted-sln-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Demo.slnx"), "<Solution />");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Demo.cs"), "public sealed class Demo {}");

            var store = new InMemoryGraphStore();
            var solutionAnalyzer = new RecordingSolutionAnalyzer();
            var logger = new RecordingLogger<IndexingPipeline>();
            var pipeline = new IndexingPipeline(
                store,
                [new RoslynExtractor()],
                Options.Create(new IndexingOptions()),
                new LocalFileSystem(),
                logger,
                solutionAnalyzer);

            await pipeline.IndexProjectAsync(
                "Demo", rootPath, repositoryToolingIdentity: "folder:Demo", ct: CancellationToken.None);

            solutionAnalyzer.CalledSolutionPath.ShouldBeNull();
            store.Nodes.ShouldContain(node => node.Name == "Demo" && node.Label == NodeLabel.Class);
            logger.Messages.ShouldContain(message =>
                message.Contains("SECURITY-AUDIT", StringComparison.Ordinal)
                && message.Contains("folder:Demo", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_AssignsStructuralFilesToNearestDotnetProject()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-dotnet-project-map-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var appDir = Path.Combine(rootPath, "src", "Demo.App");
            Directory.CreateDirectory(appDir);

            await File.WriteAllTextAsync(Path.Combine(appDir, "Demo.App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(appDir, "Program.cs"), "public class Program {}");

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new TestSupportMetadataExtractor(new DotnetSupportInfo("supported", "supported", null, []))],
                Options.Create(new IndexingOptions()),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("DemoRepo", rootPath, ct: CancellationToken.None);

            var counts = await store.GetNodeCountsByDotnetProjectAsync("DemoRepo");
            counts.ShouldContainKey("Demo.App");
            counts["Demo.App"].ShouldContainKey(nameof(NodeLabel.File));
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_PersistsCargoPackagesAndDependencyEdges()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-cargo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(rootPath, "src"));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Cargo.toml"), """
                [package]
                name = "consumer"
                version = "0.1.0"

                [dependencies]
                provider_alias = { package = "provider", version = "1.2.3" }
                """);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "src", "lib.rs"),
                "pub fn run() {}\n");

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new MixedRustPythonMetadataExtractor()],
                Options.Create(new IndexingOptions()),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                cargoManifestExtractor: new CargoManifestExtractor());

            await pipeline.IndexProjectAsync("Consumer", rootPath, ct: CancellationToken.None);

            var packageNodes = store.Nodes.Where(node => node.Label == NodeLabel.Package).ToList();
            var consumer = packageNodes.Single(node => node.Name == "consumer");
            var provider = packageNodes.Single(node => node.Name == "provider");
            provider.Properties["local_name"].ShouldBe("provider_alias");
            provider.Properties["package_key"].ShouldBe("cargo:registry:crates.io:provider");
            store.Edges.ShouldContain(edge =>
                edge.SourceId == consumer.Id &&
                edge.TargetId == provider.Id &&
                edge.Type == EdgeType.REFERENCES_PACKAGE);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_IncrementalRun_ReplacesChangedCargoDependencies()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-cargo-incremental-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(rootPath, "src"));

        try
        {
            static string Manifest(string dependency) => $"""
                [package]
                name = "consumer"
                version = "0.1.0"

                [dependencies]
                {dependency} = "1"
                """;
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Cargo.toml"), Manifest("serde"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "src", "lib.rs"), "pub fn run() {}\n");

            var store = new InMemoryGraphStore();
            IndexingPipeline CreatePipeline() => new(
                store,
                [new MixedRustPythonMetadataExtractor()],
                Options.Create(new IndexingOptions()),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                cargoManifestExtractor: new CargoManifestExtractor());

            await CreatePipeline().IndexProjectAsync("Consumer", rootPath, ct: CancellationToken.None);
            store.Nodes.ShouldContain(node => node.Label == NodeLabel.Package && node.Name == "serde");

            // Only Cargo.toml changes; lib.rs is unchanged, so nothing is re-extracted per file.
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Cargo.toml"), Manifest("tokio"));
            await CreatePipeline().IndexProjectAsync("Consumer", rootPath, ct: CancellationToken.None);

            var packages = store.Nodes.Where(node => node.Label == NodeLabel.Package).ToList();
            packages.Select(node => node.Name).ShouldBe(["consumer", "tokio"], ignoreOrder: true);
            var consumer = packages.Single(node => node.Name == "consumer");
            var tokio = packages.Single(node => node.Name == "tokio");
            store.Edges.ShouldContain(edge =>
                edge.SourceId == consumer.Id && edge.TargetId == tokio.Id &&
                edge.Type == EdgeType.REFERENCES_PACKAGE);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("rust_semantic_command_timeout",
        "Rust semantic command 'rust-analyzer' exceeded the 600-second timeout.",
        RustSemanticFailureCategory.Timeout)]
    [InlineData("rust_semantic_resource_exhausted",
        "rust-analyzer scip was killed with exit 137 (SIGKILL), consistent with container memory exhaustion.",
        RustSemanticFailureCategory.ResourceExhausted)]
    [InlineData("rust_analyzer_scip_failed",
        "rust-analyzer scip exited with 1: error: failed to run custom build command for `alpha v0.1.0`",
        RustSemanticFailureCategory.BuildScript)]
    [InlineData("rust_semantic_tools_unavailable",
        "Rust semantic indexing tools are unavailable (rust-analyzer: False, scip: True).",
        RustSemanticFailureCategory.Toolchain)]
    [InlineData("rust_analyzer_scip_failed",
        "rust-analyzer scip exited with 101: thread 'main' panicked",
        RustSemanticFailureCategory.Other)]
    public async Task IndexProjectAsync_RustSemanticFailure_FallsBackToLowConfidenceTreeSitterNodes(
        string detailCode,
        string message,
        string expectedFailureCode)
    {
        var rootPath = await CreateRustWorkspaceFixtureAsync("codegraph-rust-fallback");
        try
        {
            var store = new InMemoryGraphStore();
            var diagnostics = new IndexingRunDiagnostics();
            var pipeline = CreateRustFallbackPipeline(
                store,
                diagnostics,
                new ThrowingRustAnalyzer(new RustSemanticIndexingException(detailCode, message)));

            await pipeline.IndexProjectAsync("RustFallback", rootPath, ct: CancellationToken.None);

            AssertLowConfidenceTreeSitterNodes(store, "RustFallback", expectedFailureCode);
            var fallback = diagnostics.RustSemanticFallbacks.ShouldHaveSingleItem();
            fallback.FailureCode.ShouldBe(expectedFailureCode);
            fallback.DetailCode.ShouldBe(detailCode);
            fallback.Manifest.ShouldBe("Cargo.toml");
            fallback.Crates.ShouldBe(["alpha", "beta"]);
            fallback.Message.ShouldContain(message);

            var repository = await store.GetRepositoryByName("RustFallback");
            repository.ShouldNotBeNull();
            repository.Properties.ShouldNotBeNull();
            var recorded = ((JsonElement)repository.Properties["rustSemanticFallbacks"]).EnumerateArray().Single();
            recorded.GetProperty("failureCode").GetString().ShouldBe(expectedFailureCode);
            recorded.GetProperty("crates").EnumerateArray().Select(crate => crate.GetString())
                .ShouldBe(["alpha", "beta"]);

            // No group could run: one whole-workspace fallback outcome, settings from the options.
            var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
            var group = outcomes.Groups.ShouldHaveSingleItem();
            group.Outcome.ShouldBe(RustSemanticGroupOutcome.Fallback);
            group.Crates.ShouldBe(["alpha", "beta"]);
            (group.FailureCode, group.DetailCode).ShouldBe((expectedFailureCode, detailCode));
            (outcomes.CrateCount, outcomes.FallbackGroups).ShouldBe((2, 1));
            outcomes.Settings.ShouldBe(new RustSemanticSettingsSnapshot(8, 600, 3600, 8192, false, false));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_ConsumerPathRustFallback_RecordsStructuredWarningAndRepositoryProperties()
    {
        // ProcessRepository (scheduled/CI) indexing: incremental, no IndexingRunDiagnostics and
        // no indexer_runs row, so the log and repository properties are the only record.
        var rootPath = await CreateRustWorkspaceFixtureAsync("codegraph-rust-consumer-fallback");
        try
        {
            var store = new InMemoryGraphStore();
            var logger = new RecordingLogger<IndexingPipeline>();
            var pipeline = CreateRustFallbackPipeline(
                store,
                diagnostics: null,
                new ThrowingRustAnalyzer(new RustSemanticIndexingException(
                    "rust_semantic_command_timeout", "rust-analyzer timed out")),
                logger);

            await pipeline.IndexProjectAsync("RustConsumerFallback", rootPath, ct: CancellationToken.None);

            var warning = logger.Entries.Single(entry =>
                entry.Level == LogLevel.Warning && entry.Properties.ContainsKey("FailureCode"));
            warning.Properties["FailureCode"].ShouldBe(RustSemanticFailureCategory.Timeout);
            warning.Properties["DetailCode"].ShouldBe("rust_semantic_command_timeout");
            warning.Properties["Project"].ShouldBe("RustConsumerFallback");
            warning.Properties["Manifest"].ShouldBe("Cargo.toml");
            ((IEnumerable<string>)warning.Properties["Crates"]!).ShouldBe(["alpha", "beta"]);

            var repository = await store.GetRepositoryByName("RustConsumerFallback");
            repository.ShouldNotBeNull();
            repository.Properties.ShouldNotBeNull();
            var recorded = ((JsonElement)repository.Properties["rustSemanticFallbacks"]).EnumerateArray().Single();
            recorded.GetProperty("failureCode").GetString().ShouldBe(RustSemanticFailureCategory.Timeout);
            recorded.GetProperty("crates").EnumerateArray().Select(crate => crate.GetString())
                .ShouldBe(["alpha", "beta"]);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_RustFallbackWithoutOtherMetadata_StillRecordsRepositoryProperties()
    {
        // No .rs sources and no Cargo manifest extractor: nothing else yields project metadata,
        // yet the fallback record must still reach repositories.properties.
        var rootPath = await CreateRustWorkspaceFixtureAsync("codegraph-rust-nometadata-fallback");
        try
        {
            foreach (var source in Directory.GetFiles(rootPath, "*.rs", SearchOption.AllDirectories))
                File.Delete(source);
            var store = new InMemoryGraphStore();
            var pipeline = CreateRustFallbackPipeline(
                store,
                diagnostics: null,
                new ThrowingRustAnalyzer(new RustSemanticIndexingException(
                    "rust_semantic_command_timeout", "rust-analyzer timed out")),
                includeCargoManifestExtractor: false);

            await pipeline.IndexProjectAsync("RustNoMetadataFallback", rootPath, ct: CancellationToken.None);

            var repository = await store.GetRepositoryByName("RustNoMetadataFallback");
            repository.ShouldNotBeNull();
            repository.Properties.ShouldNotBeNull();
            var recorded = ((JsonElement)repository.Properties["rustSemanticFallbacks"]).EnumerateArray().Single();
            recorded.GetProperty("failureCode").GetString().ShouldBe(RustSemanticFailureCategory.Timeout);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_GenericRustAnalyzerFailure_FallsBackWithOtherFailureCode()
    {
        var rootPath = await CreateRustWorkspaceFixtureAsync("codegraph-rust-generic-fallback");
        try
        {
            var store = new InMemoryGraphStore();
            var diagnostics = new IndexingRunDiagnostics();
            var pipeline = CreateRustFallbackPipeline(
                store,
                diagnostics,
                new ThrowingRustAnalyzer(new InvalidOperationException("Synthetic generic analyzer failure.")));

            await pipeline.IndexProjectAsync(
                "RustGenericFallback", rootPath, replaceExistingGraph: true, ct: CancellationToken.None);

            AssertLowConfidenceTreeSitterNodes(store, "RustGenericFallback", RustSemanticFailureCategory.Other);
            var fallback = diagnostics.RustSemanticFallbacks.ShouldHaveSingleItem();
            fallback.FailureCode.ShouldBe(RustSemanticFailureCategory.Other);
            fallback.DetailCode.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_MalformedLazyRustResult_FallsBackWithoutPartialSemanticGraph()
    {
        var rootPath = await CreateRustWorkspaceFixtureAsync("codegraph-rust-partial-fallback");
        try
        {
            var store = new InMemoryGraphStore();
            var diagnostics = new IndexingRunDiagnostics();
            var pipeline = CreateRustFallbackPipeline(store, diagnostics, new PartiallyEnumeratingRustAnalyzer());

            await pipeline.IndexProjectAsync("RustPartialFallback", rootPath, ct: CancellationToken.None);

            store.Nodes.ShouldNotContain(node => node.Name == "partial-semantic-node");
            AssertLowConfidenceTreeSitterNodes(store, "RustPartialFallback", RustSemanticFailureCategory.Other);
            diagnostics.RustSemanticFallbacks.ShouldHaveSingleItem();
            // A group the pipeline could not merge is a fallback, not semantic.
            var group = diagnostics.RustSemanticOutcomes.ShouldNotBeNull().Groups.ShouldHaveSingleItem();
            group.Outcome.ShouldBe(RustSemanticGroupOutcome.Fallback);
            group.FailureCode.ShouldBe(RustSemanticFailureCategory.Other);
            group.Documents.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_RustSemanticSuccess_DoesNotMarkNodesLowConfidence()
    {
        var rootPath = await CreateRustWorkspaceFixtureAsync("codegraph-rust-semantic-success");
        try
        {
            var store = new InMemoryGraphStore();
            var diagnostics = new IndexingRunDiagnostics();
            var pipeline = CreateRustFallbackPipeline(store, diagnostics, new SucceedingRustAnalyzer());

            await pipeline.IndexProjectAsync("RustSemantic", rootPath, ct: CancellationToken.None);

            diagnostics.RustSemanticFallbacks.ShouldBeEmpty();
            store.Nodes.ShouldContain(node => node.Name == "semantic_alpha");
            store.Nodes.ShouldNotContain(node => GraphNodeConfidence.IsLowConfidence(node));
            var repository = await store.GetRepositoryByName("RustSemantic");
            (repository!.Properties ?? []).ShouldNotContainKey("rustSemanticFallbacks");

            // A whole-workspace analyzer still reports one semantic group covering every crate.
            var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
            var group = outcomes.Groups.ShouldHaveSingleItem();
            group.Outcome.ShouldBe(RustSemanticGroupOutcome.Semantic);
            group.Crates.ShouldBe(["alpha", "beta"]);
            (group.Documents, group.Nodes, group.Edges).ShouldBe((2, 1, 0));
            group.DurationMs.ShouldBe(outcomes.TotalDurationMs);
            group.DurationMs.ShouldBeGreaterThanOrEqualTo(SucceedingRustAnalyzer.AnalysisDelayMs);
            outcomes.CrateCount.ShouldBe(2);
            repository.Properties!.ShouldContainKey("rustSemanticOutcomes");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static async Task<string> CreateRustWorkspaceFixtureAsync(string prefix)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        foreach (var crate in new[] { "alpha", "beta" })
        {
            var crateRoot = Path.Combine(rootPath, "crates", crate);
            Directory.CreateDirectory(Path.Combine(crateRoot, "src"));
            await File.WriteAllTextAsync(Path.Combine(crateRoot, "Cargo.toml"),
                $"[package]\nname = \"{crate}\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
            await File.WriteAllTextAsync(Path.Combine(crateRoot, "src", "lib.rs"),
                $"pub struct {char.ToUpperInvariant(crate[0])}{crate[1..]}Widget;\n\npub fn {crate}_value() -> i32 {{ 1 }}\n");
        }

        await File.WriteAllTextAsync(Path.Combine(rootPath, "Cargo.toml"),
            "[workspace]\nmembers = [\"crates/alpha\", \"crates/beta\"]\nresolver = \"2\"\n");
        return rootPath;
    }

    private static IndexingPipeline CreateRustFallbackPipeline(
        InMemoryGraphStore store,
        IndexingRunDiagnostics? diagnostics,
        IRustAnalyzer rustAnalyzer,
        ILogger<IndexingPipeline>? logger = null,
        bool includeCargoManifestExtractor = true) =>
        new(
            store,
            [new CodeGraph.Extractors.TreeSitter.TreeSitterExtractor(
                NullLogger<CodeGraph.Extractors.TreeSitter.TreeSitterExtractor>.Instance)],
            Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
            new LocalFileSystem(),
            logger ?? NullLogger<IndexingPipeline>.Instance,
            rustAnalyzer: rustAnalyzer,
            cargoManifestExtractor: includeCargoManifestExtractor ? new CargoManifestExtractor() : null,
            diagnostics: diagnostics);

    private static void AssertLowConfidenceTreeSitterNodes(
        InMemoryGraphStore store,
        string project,
        string expectedFailureCode)
    {
        foreach (var crate in new[] { "alpha", "beta" })
        {
            var filePath = $"crates/{crate}/src/lib.rs";
            var codeNodes = store.Nodes
                .Where(node => node.Project == project && node.FilePath == filePath &&
                               node.Label is not (NodeLabel.File or NodeLabel.Folder))
                .ToList();
            codeNodes.ShouldContain(node => node.Name == $"{crate}_value" && node.Label == NodeLabel.Function);
            codeNodes.ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
            codeNodes.ShouldAllBe(node =>
                (string)node.Properties[GraphNodeConfidence.ExtractionSourceProperty] ==
                GraphNodeConfidence.TreeSitterFallbackSource);
            codeNodes.ShouldAllBe(node =>
                (string)node.Properties[GraphNodeConfidence.FallbackReasonProperty] == expectedFailureCode);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_ReplacementRemovesStaleNodesHashesAndAnalysis()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-full-index-reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var stalePath = Path.Combine(rootPath, "app.py");
            await File.WriteAllTextAsync(stalePath, "class FastApiBackend:\n    pass\n");

            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(
                store,
                [new NodeProducingExtractor()],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);

            await pipeline.IndexProjectAsync("SceneWorks", rootPath,
                replaceExistingGraph: true, ct: CancellationToken.None);

            (await store.FindNodesByFileAsync("SceneWorks", "app.py")).ShouldNotBeEmpty();
            (await store.GetFileHashesAsync("SceneWorks")).ShouldContainKey("app.py");
            var staleNode = (await store.FindNodesByFileAsync("SceneWorks", "app.py"))
                .First(node => node.Label == NodeLabel.Class);
            var dependencyNodeIds = await store.UpsertNodeBatchAsync(
            [
                new GraphNode
                {
                    Project = "Dependency",
                    Label = NodeLabel.Class,
                    Name = "Dependency",
                    QualifiedName = "Dependency.Root",
                    FilePath = "dependency.rs"
                }
            ]);
            await store.InsertCrossRepoEdgeAsync(new CrossRepoEdge
            {
                SourceProject = "SceneWorks",
                TargetProject = "Dependency",
                SourceNodeId = staleNode.Id,
                TargetNodeId = dependencyNodeIds[GraphNodeKey.Create("Dependency", "Dependency.Root")],
                Type = EdgeType.CALLS
            });
            await store.UpsertNodeAnalysisAsync(new NodeAnalysisEntity
            {
                NodeId = staleNode.Id,
                Description = "stale FastAPI analysis",
                Confidence = "high"
            });

            File.Delete(stalePath);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "main.rs"), "pub fn axum_sidecar() {}\n");

            await pipeline.IndexProjectAsync("SceneWorks", rootPath,
                replaceExistingGraph: true, ct: CancellationToken.None);

            (await store.FindNodesByFileAsync("SceneWorks", "app.py")).ShouldBeEmpty();
            (await store.FindNodesByFileAsync("SceneWorks", "main.rs")).ShouldNotBeEmpty();
            var hashes = await store.GetFileHashesAsync("SceneWorks");
            hashes.ShouldNotContainKey("app.py");
            hashes.ShouldContainKey("main.rs");
            (await store.GetNodeAnalysisAsync(staleNode.Id)).ShouldBeNull();
            (await store.FindCrossRepoEdgesAsync("SceneWorks")).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_ReplacementFailurePreservesExistingGraph()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-replacement-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var filePath = Path.Combine(rootPath, "app.py");
            await File.WriteAllTextAsync(filePath, "class ExistingBackend:\n    pass\n");
            var store = new InMemoryGraphStore();
            var goodPipeline = CreatePipeline(store, new NodeProducingExtractor());

            await goodPipeline.IndexProjectAsync("FailureSafeRepo", rootPath,
                replaceExistingGraph: true, ct: CancellationToken.None);
            var nodesBefore = store.Nodes
                .Where(node => node.Project == "FailureSafeRepo")
                .Select(node => node.QualifiedName)
                .Order()
                .ToList();
            var hashesBefore = await store.GetFileHashesAsync("FailureSafeRepo");
            var repositoryBefore = await store.GetRepositoryByName("FailureSafeRepo");

            await File.WriteAllTextAsync(filePath, "class BrokenReplacement:\n    pass\n");
            var failingPipeline = CreatePipeline(store, new ThrowingExtractor());

            await Should.ThrowAsync<AggregateException>(() =>
                failingPipeline.IndexProjectAsync("FailureSafeRepo", rootPath,
                    replaceExistingGraph: true, ct: CancellationToken.None));

            store.Nodes.Where(node => node.Project == "FailureSafeRepo")
                .Select(node => node.QualifiedName)
                .Order()
                .ShouldBe(nodesBefore);
            (await store.GetFileHashesAsync("FailureSafeRepo")).ShouldBe(hashesBefore);
            (await store.GetRepositoryByName("FailureSafeRepo")).ShouldBe(repositoryBefore);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_ReplacementStoreFailurePreservesExistingSnapshot()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-replacement-store-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var filePath = Path.Combine(rootPath, "app.py");
            await File.WriteAllTextAsync(filePath, "class ExistingBackend:\n    pass\n");
            var store = new InMemoryGraphStore();
            var pipeline = CreatePipeline(store, new NodeProducingExtractor());
            await pipeline.IndexProjectAsync("StoreFailureRepo", rootPath,
                replaceExistingGraph: true,
                replacementSyncState: new SyncStateEntity
                {
                    Project = "StoreFailureRepo",
                    LastCommitSha = "old-commit",
                    LastSyncAt = DateTime.UtcNow,
                    Status = "idle"
                },
                ct: CancellationToken.None);

            var nodesBefore = store.Nodes
                .Where(node => node.Project == "StoreFailureRepo")
                .Select(node => node.QualifiedName)
                .Order()
                .ToList();
            var hashesBefore = await store.GetFileHashesAsync("StoreFailureRepo");
            var repositoryBefore = await store.GetRepositoryByName("StoreFailureRepo");
            var syncStateBefore = await store.GetSyncStateAsync("StoreFailureRepo");

            await File.WriteAllTextAsync(filePath, "class NewBackend:\n    pass\n");
            store.ReplacementFailure = new InvalidOperationException("synthetic store failure");

            await Should.ThrowAsync<InvalidOperationException>(() =>
                pipeline.IndexProjectAsync("StoreFailureRepo", rootPath,
                    replaceExistingGraph: true,
                    replacementSyncState: new SyncStateEntity
                    {
                        Project = "StoreFailureRepo",
                        LastCommitSha = "new-commit",
                        LastSyncAt = DateTime.UtcNow,
                        Status = "idle"
                    },
                    ct: CancellationToken.None));

            store.Nodes.Where(node => node.Project == "StoreFailureRepo")
                .Select(node => node.QualifiedName)
                .Order()
                .ShouldBe(nodesBefore);
            (await store.GetFileHashesAsync("StoreFailureRepo")).ShouldBe(hashesBefore);
            (await store.GetRepositoryByName("StoreFailureRepo")).ShouldBe(repositoryBefore);
            (await store.GetSyncStateAsync("StoreFailureRepo")).ShouldBe(syncStateBefore);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_RoutineIndexRemainsHashIncremental()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-incremental-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "app.py"), "class Backend:\n    pass\n");
            var store = new InMemoryGraphStore();
            var extractor = new CountingExtractor();
            var pipeline = CreatePipeline(store, extractor);

            await pipeline.IndexProjectAsync("IncrementalRepo", rootPath, ct: CancellationToken.None);
            await pipeline.IndexProjectAsync("IncrementalRepo", rootPath, ct: CancellationToken.None);

            extractor.CallCount.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_ChangedFileAtomicallyReplacesSymbolsRoutesAndDependentEdges()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-slice-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "api.cg"), "old");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "caller.cg"), "caller");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "target.cg"), "target");
            var store = new InMemoryGraphStore();
            var pipeline = CreatePipeline(store, new ReplacementGraphExtractor());

            await pipeline.IndexProjectAsync("SliceRepo", rootPath, ct: CancellationToken.None);

            var unchangedCaller = (await store.FindNodeByQualifiedNameAsync("SliceRepo", "SliceRepo.Caller.Run"))!;
            var unchangedTarget = (await store.FindNodeByQualifiedNameAsync("SliceRepo", "SliceRepo.Target.Run"))!;
            var oldClass = (await store.FindNodeByQualifiedNameAsync("SliceRepo", "SliceRepo.OldController"))!;
            var oldMethod = (await store.FindNodeByQualifiedNameAsync("SliceRepo", "SliceRepo.OldController.OldAction"))!;
            var oldRoute = (await store.FindNodeByQualifiedNameAsync("SliceRepo", "route:GET:/old"))!;
            (await store.FindEdgesBySourceAsync(oldMethod.Id, EdgeType.CALLS))
                .ShouldContain(edge => edge.TargetId == unchangedTarget.Id);
            (await store.FindEdgesBySourceAsync(unchangedCaller.Id, EdgeType.CALLS))
                .ShouldContain(edge => edge.TargetId == oldMethod.Id);
            var oldHash = (await store.GetFileHashesAsync("SliceRepo"))["api.cg"];

            await File.WriteAllTextAsync(Path.Combine(rootPath, "api.cg"), "new");
            await pipeline.IndexProjectAsync("SliceRepo", rootPath, ct: CancellationToken.None);

            (await store.FindNodeByQualifiedNameAsync("SliceRepo", oldClass.QualifiedName)).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceRepo", oldMethod.QualifiedName)).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceRepo", oldRoute.QualifiedName)).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceRepo", "SliceRepo.NewController")).ShouldNotBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceRepo", "SliceRepo.NewController.NewAction")).ShouldNotBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceRepo", "route:GET:/new")).ShouldNotBeNull();
            (await store.FindNodeByIdAsync(unchangedCaller.Id)).ShouldNotBeNull();
            (await store.FindNodeByIdAsync(unchangedTarget.Id)).ShouldNotBeNull();
            store.Edges.ShouldNotContain(edge => edge.SourceId == oldMethod.Id || edge.TargetId == oldMethod.Id);
            (await store.GetFileHashesAsync("SliceRepo"))["api.cg"].ShouldNotBe(oldHash);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_ExtractionOrStoreFailurePreservesPriorSliceAndHash()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-slice-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var filePath = Path.Combine(rootPath, "api.cg");
            await File.WriteAllTextAsync(filePath, "old");
            var store = new InMemoryGraphStore();
            await CreatePipeline(store, new ReplacementGraphExtractor())
                .IndexProjectAsync("FailureSliceRepo", rootPath, ct: CancellationToken.None);
            var oldNode = (await store.FindNodeByQualifiedNameAsync(
                "FailureSliceRepo", "FailureSliceRepo.OldController.OldAction"))!;
            var oldHash = (await store.GetFileHashesAsync("FailureSliceRepo"))["api.cg"];

            await File.WriteAllTextAsync(filePath, "new");
            await CreatePipeline(store, new AlwaysFailingReplacementExtractor())
                .IndexProjectAsync("FailureSliceRepo", rootPath, ct: CancellationToken.None);
            (await store.FindNodeByIdAsync(oldNode.Id)).ShouldNotBeNull();
            (await store.GetFileHashesAsync("FailureSliceRepo"))["api.cg"].ShouldBe(oldHash);

            await CreatePipeline(store, new ReportedFailureReplacementExtractor())
                .IndexProjectAsync("FailureSliceRepo", rootPath, ct: CancellationToken.None);
            (await store.FindNodeByIdAsync(oldNode.Id)).ShouldNotBeNull();
            (await store.GetFileHashesAsync("FailureSliceRepo"))["api.cg"].ShouldBe(oldHash);

            store.IncrementalReplacementFailure = new InvalidOperationException("synthetic atomic write failure");
            await Should.ThrowAsync<InvalidOperationException>(() =>
                CreatePipeline(store, new ReplacementGraphExtractor())
                    .IndexProjectAsync("FailureSliceRepo", rootPath, ct: CancellationToken.None));
            (await store.FindNodeByIdAsync(oldNode.Id)).ShouldNotBeNull();
            (await store.GetFileHashesAsync("FailureSliceRepo"))["api.cg"].ShouldBe(oldHash);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_IncrementalDeletionRemovesNodesHashesAndAnalysis()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-incremental-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var filePath = Path.Combine(rootPath, "app.py");
            await File.WriteAllTextAsync(filePath, "class Backend:\n    pass\n");
            var store = new InMemoryGraphStore();
            var pipeline = CreatePipeline(store, new NodeProducingExtractor());
            await pipeline.IndexProjectAsync("DeleteRepo", rootPath, ct: CancellationToken.None);
            var staleNode = (await store.FindNodesByFileAsync("DeleteRepo", "app.py"))
                .First(node => node.Label == NodeLabel.Class);
            var dependencyNodeIds = await store.UpsertNodeBatchAsync(
            [
                new GraphNode
                {
                    Project = "Dependency",
                    Label = NodeLabel.Class,
                    Name = "Dependency",
                    QualifiedName = "Dependency.Root",
                    FilePath = "dependency.rs"
                }
            ]);
            await store.InsertCrossRepoEdgeAsync(new CrossRepoEdge
            {
                SourceProject = "DeleteRepo",
                TargetProject = "Dependency",
                SourceNodeId = staleNode.Id,
                TargetNodeId = dependencyNodeIds[GraphNodeKey.Create("Dependency", "Dependency.Root")],
                Type = EdgeType.CALLS
            });
            await store.UpsertNodeAnalysisAsync(new NodeAnalysisEntity
            {
                NodeId = staleNode.Id,
                Description = "stale",
                Confidence = "high"
            });

            File.Delete(filePath);
            await pipeline.IndexProjectAsync("DeleteRepo", rootPath,
                changedFilesOnly: ["app.py"], ct: CancellationToken.None);

            (await store.FindNodesByFileAsync("DeleteRepo", "app.py")).ShouldBeEmpty();
            (await store.GetFileHashesAsync("DeleteRepo")).ShouldNotContainKey("app.py");
            (await store.GetNodeAnalysisAsync(staleNode.Id)).ShouldBeNull();
            (await store.FindCrossRepoEdgesAsync("DeleteRepo")).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_IncrementalDeletionReconcilesWindowsPathSeparators()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-windows-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var store = new InMemoryGraphStore();
            await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "WindowsPathRepo" });
            var nodeIds = await store.UpsertNodeBatchAsync(
            [
                new GraphNode
                {
                    Project = "WindowsPathRepo",
                    Label = NodeLabel.Class,
                    Name = "Backend",
                    QualifiedName = "WindowsPathRepo.Backend",
                    FilePath = "src\\app.py"
                }
            ]);
            var nodeId = nodeIds[GraphNodeKey.Create("WindowsPathRepo", "WindowsPathRepo.Backend")];
            await store.UpsertNodeAnalysisAsync(new NodeAnalysisEntity
            {
                NodeId = nodeId,
                Description = "stale",
                Confidence = "high"
            });
            await store.UpsertFileHashBatchAsync("WindowsPathRepo", new Dictionary<string, string>
            {
                ["src\\app.py"] = "old-hash"
            });

            var pipeline = CreatePipeline(store, new NodeProducingExtractor());
            await pipeline.IndexProjectAsync("WindowsPathRepo", rootPath,
                changedFilesOnly: ["src/app.py"], ct: CancellationToken.None);

            (await store.FindNodesByFileAsync("WindowsPathRepo", "src\\app.py")).ShouldBeEmpty();
            (await store.GetFileHashesAsync("WindowsPathRepo")).ShouldBeEmpty();
            (await store.GetNodeAnalysisAsync(nodeId)).ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_FullScanIncremental_RemovesFilesDeletedFromDisk()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-fullscan-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "keep.py"), "class Keep:\n    pass\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "gone.py"), "class Gone:\n    pass\n");
            var store = new InMemoryGraphStore();
            var pipeline = CreatePipeline(store, new NodeProducingExtractor());
            await pipeline.IndexProjectAsync("FullScanRepo", rootPath, ct: CancellationToken.None);
            var goneIds = (await store.FindNodesByFileAsync("FullScanRepo", "gone.py"))
                .Select(node => node.Id).ToHashSet();
            goneIds.ShouldNotBeEmpty();
            store.Edges.ShouldContain(edge => goneIds.Contains(edge.SourceId) || goneIds.Contains(edge.TargetId));

            File.Delete(Path.Combine(rootPath, "gone.py"));
            await pipeline.IndexProjectAsync("FullScanRepo", rootPath, ct: CancellationToken.None);

            (await store.FindNodesByFileAsync("FullScanRepo", "gone.py")).ShouldBeEmpty();
            store.Edges.ShouldNotContain(edge => goneIds.Contains(edge.SourceId) || goneIds.Contains(edge.TargetId));
            var hashes = await store.GetFileHashesAsync("FullScanRepo");
            hashes.ShouldNotContainKey("gone.py");
            hashes.ShouldContainKey("keep.py");
            (await store.FindNodesByFileAsync("FullScanRepo", "keep.py"))
                .ShouldContain(node => node.Label == NodeLabel.Class);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_FullScanIncremental_RemovesFilesANewSkipPatternExcludes()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-fullscan-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(rootPath, "vendor"));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "app.py"), "class App:\n    pass\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "vendor", "lib.py"), "class Lib:\n    pass\n");
            var store = new InMemoryGraphStore();
            await CreatePipeline(store, new NodeProducingExtractor())
                .IndexProjectAsync("SkipRepo", rootPath, ct: CancellationToken.None);
            (await store.FindNodesByFileAsync("SkipRepo", "vendor/lib.py")).ShouldNotBeEmpty();

            var skipping = new IndexingPipeline(
                store,
                [new NodeProducingExtractor()],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1, SkipPatterns = ["vendor/**"] }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);
            await skipping.IndexProjectAsync("SkipRepo", rootPath, ct: CancellationToken.None);

            (await store.FindNodesByFileAsync("SkipRepo", "vendor/lib.py")).ShouldBeEmpty();
            (await store.GetFileHashesAsync("SkipRepo")).ShouldNotContainKey("vendor/lib.py");
            (await store.FindNodesByFileAsync("SkipRepo", "app.py")).ShouldNotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_FullScanIncremental_MissingCheckoutFailsWithoutDeleting()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-fullscan-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "app.py"), "class App:\n    pass\n");
            var store = new InMemoryGraphStore();
            var pipeline = CreatePipeline(store, new NodeProducingExtractor());
            await pipeline.IndexProjectAsync("MissingRepo", rootPath, ct: CancellationToken.None);
            var nodeCount = store.Nodes.Count;

            Directory.Delete(rootPath, recursive: true);
            // Fails up front, naming the missing checkout, before any deletion is inferred.
            var error = await Should.ThrowAsync<DirectoryNotFoundException>(() =>
                pipeline.IndexProjectAsync("MissingRepo", rootPath, ct: CancellationToken.None));
            error.Message.ShouldContain("checkout not found");

            store.Nodes.Count.ShouldBe(nodeCount);
            (await store.GetFileHashesAsync("MissingRepo")).ShouldContainKey("app.py");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_FullScanIncremental_EmptyDiscoveryKeepsTheIndexedGraph()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-fullscan-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "app.py"), "class App:\n    pass\n");
            var store = new InMemoryGraphStore();
            var pipeline = CreatePipeline(store, new NodeProducingExtractor());
            await pipeline.IndexProjectAsync("EmptyRepo", rootPath, ct: CancellationToken.None);

            // The checkout exists but yields nothing (e.g. an interrupted materialization).
            File.Delete(Path.Combine(rootPath, "app.py"));
            await pipeline.IndexProjectAsync("EmptyRepo", rootPath, ct: CancellationToken.None);

            (await store.FindNodesByFileAsync("EmptyRepo", "app.py")).ShouldNotBeEmpty();
            (await store.GetFileHashesAsync("EmptyRepo")).ShouldContainKey("app.py");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_SerializesReplacementForSameRepository()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-replacement-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "app.py"), "class Backend:\n    pass\n");
            var extractor = new ConcurrencyTrackingExtractor();
            var pipeline = CreatePipeline(new InMemoryGraphStore(), extractor);

            await Task.WhenAll(
                pipeline.IndexProjectAsync("SerializedRepo", rootPath,
                    replaceExistingGraph: true, ct: CancellationToken.None),
                pipeline.IndexProjectAsync("SerializedRepo", rootPath,
                    replaceExistingGraph: true, ct: CancellationToken.None));

            extractor.MaxConcurrent.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_ExtractionVersionBump_ReextractsUnchangedFilesOnce()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-extraction-version-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "one.py"), "class One:\n    pass\n");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "two.py"), "class Two:\n    pass\n");
            var store = new InMemoryGraphStore();
            var extractor = new VersionedExtractor();

            await CreatePipeline(store, extractor).IndexProjectAsync("VersionRepo", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(2);

            // Matching version: unchanged files stay incremental.
            await CreatePipeline(store, extractor).IndexProjectAsync("VersionRepo", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(2);

            // Deploying a bumped extractor re-extracts every unchanged file exactly once.
            extractor.Versions[".py"] = "2";
            var bumped = CreatePipeline(store, extractor);
            await bumped.IndexProjectAsync("VersionRepo", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(4);
            (await store.GetFileHashRecordsAsync("VersionRepo"))["one.py"].ExtractionVersion
                .ShouldBe($"p{ExtractionVersionCatalog.PipelineVersion};VersionedExtractor=2");

            await bumped.IndexProjectAsync("VersionRepo", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(4);
            (await store.FindNodeByQualifiedNameAsync("VersionRepo", "VersionRepo.one")).ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_MissingStoredExtractionVersion_ReextractsUnchangedFiles()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-extraction-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "one.py"), "class One:\n    pass\n");
            var store = new InMemoryGraphStore();
            var extractor = new VersionedExtractor();
            var pipeline = CreatePipeline(store, extractor);
            await pipeline.IndexProjectAsync("LegacyRepo", rootPath, ct: CancellationToken.None);

            // Rows written before extraction versioning have no version.
            store.ClearFileExtractionVersions("LegacyRepo");
            await pipeline.IndexProjectAsync("LegacyRepo", rootPath, ct: CancellationToken.None);

            extractor.CallCount.ShouldBe(2);
            (await store.GetFileHashRecordsAsync("LegacyRepo"))["one.py"].ExtractionVersion.ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_FullReplacementRecordsExtractionVersions()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-extraction-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "one.py"), "class One:\n    pass\n");
            var store = new InMemoryGraphStore();
            var extractor = new VersionedExtractor();
            var pipeline = CreatePipeline(store, extractor);

            await pipeline.IndexProjectAsync("ReplaceRepo", rootPath, replaceExistingGraph: true, ct: CancellationToken.None);
            await pipeline.IndexProjectAsync("ReplaceRepo", rootPath, ct: CancellationToken.None);

            // The rebuild stored current versions, so the routine index stays incremental.
            extractor.CallCount.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void ExtractionVersionCatalog_ComposesSpecializedAnalyzerVersionsPerFileType()
    {
        var extractors = new ICodeExtractor[] { new RoslynExtractor(), new VersionedExtractor() };
        var v1 = new ExtractionVersionCatalog(extractors, new RecordingSolutionAnalyzer());
        var v2 = new ExtractionVersionCatalog(extractors, new RecordingSolutionAnalyzer { Version = "2" });

        v1.ForPath("src/App.cs").ShouldBe(
            $"p{ExtractionVersionCatalog.PipelineVersion};RoslynExtractor=2;RecordingSolutionAnalyzer=1");
        v2.ForPath("src/App.cs").ShouldEndWith(";RecordingSolutionAnalyzer=2");
        v1.ForPath("app.py").ShouldBe(v2.ForPath("app.py"));
        v1.Fingerprint.ShouldNotBe(v2.Fingerprint);
        new ExtractionVersionCatalog(extractors, new RecordingSolutionAnalyzer()).Fingerprint.ShouldBe(v1.Fingerprint);
    }

    [Fact]
    public async Task IndexProjectAsync_OversizeFile_IsLoggedMarkedAndNotReportedChangedAgain()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-oversize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "big.py"), new string('x', 3 * 1024));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "small.py"), "small");
            var store = new InMemoryGraphStore();
            var extractor = new VersionedExtractor(".py");
            var logger = new RecordingLogger<IndexingPipeline>();
            IndexingPipeline Pipeline(int limitKb) => new(
                store, [extractor],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1, MaxFileSizeKb = limitKb }),
                new LocalFileSystem(), logger);
            int SkipLogs() => logger.Messages.Count(message =>
                message.StartsWith("Skipping extraction of big.py", StringComparison.Ordinal));

            await Pipeline(1).IndexProjectAsync("Oversize", rootPath, ct: CancellationToken.None);

            extractor.CallCount.ShouldBe(1);
            SkipLogs().ShouldBe(1);
            var skipLog = logger.Messages.IndexOf("Skipping extraction of big.py in Oversize: 3 KB exceeds the 1 KB limit");
            skipLog.ShouldBeGreaterThanOrEqualTo(0);
            logger.Entries[skipLog].Level.ShouldBe(LogLevel.Information);
            var file = store.Nodes.Single(node => node.QualifiedName == "Oversize:big.py");
            file.Properties["skipped_reason"].ShouldBe("oversize");
            file.Properties["size_limit_kb"].ShouldBe(1);
            var hashes = await store.GetFileHashRecordsAsync("Oversize");
            hashes.Keys.ShouldBe(["big.py", "small.py"], ignoreOrder: true);
            hashes["big.py"].ExtractionVersion.ShouldEndWith(";oversize>1KB");

            // Unchanged: neither re-read as changed nor logged again.
            await Pipeline(1).IndexProjectAsync("Oversize", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(1);
            SkipLogs().ShouldBe(1);

            // A higher limit extracts it.
            await Pipeline(4).IndexProjectAsync("Oversize", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(2);
            store.Nodes.Single(node => node.QualifiedName == "Oversize:big.py")
                .Properties.ContainsKey("skipped_reason").ShouldBeFalse();
            store.Nodes.ShouldContain(node => node.QualifiedName == "Oversize.big");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// The size limits are part of the extraction fingerprint, so changing one re-runs a routine
    /// sync at an unchanged commit; that run empties an unchanged file that the lower limit makes
    /// oversize and keeps it stable afterwards.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_LoweredSizeLimit_EmptiesUnchangedFileThatIsNowOversize()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-oversize-lower-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "big.py"), new string('x', 3 * 1024));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "small.py"), "small");
            var store = new InMemoryGraphStore();
            var extractor = new VersionedExtractor(".py");
            IndexingPipeline Pipeline(int limitKb, Dictionary<string, int>? byExtension = null) => new(
                store, [extractor],
                Options.Create(new IndexingOptions
                {
                    MaxParallelFiles = 1,
                    MaxFileSizeKb = limitKb,
                    MaxFileSizeKbByExtension = byExtension ?? new(StringComparer.OrdinalIgnoreCase) { [".swift"] = 2048 }
                }),
                new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);

            await Pipeline(4).IndexProjectAsync("Lower", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(2);
            store.Nodes.ShouldContain(node => node.QualifiedName == "Lower.big");

            Pipeline(1).ExtractionFingerprint.ShouldNotBe(Pipeline(4).ExtractionFingerprint);
            Pipeline(4, new(StringComparer.OrdinalIgnoreCase) { [".swift"] = 1024 }).ExtractionFingerprint
                .ShouldNotBe(Pipeline(4).ExtractionFingerprint);
            Pipeline(4).ExtractionFingerprint.ShouldBe(Pipeline(4).ExtractionFingerprint);

            await Pipeline(1).IndexProjectAsync("Lower", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(2);
            store.Nodes.ShouldNotContain(node => node.QualifiedName == "Lower.big");
            store.Nodes.Single(node => node.QualifiedName == "Lower:big.py")
                .Properties["skipped_reason"].ShouldBe("oversize");
            store.Nodes.ShouldContain(node => node.QualifiedName == "Lower.small");
            (await store.GetFileHashRecordsAsync("Lower"))["big.py"].ExtractionVersion.ShouldEndWith(";oversize>1KB");

            // Stable under the lower limit: nothing is processed again.
            await Pipeline(1).IndexProjectAsync("Lower", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(2);
            store.Nodes.ShouldNotContain(node => node.QualifiedName == "Lower.big");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// A file over the limit that a project analyzer (not per-file extraction) processed records
    /// the oversize version too, so it is not re-processed on every later run.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_OversizeFileProcessedByProjectAnalyzer_IsNotReprocessedEveryRun()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-oversize-analyzer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "App.sln"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Big.cs"),
                "public sealed class Big {}\n" + new string('/', 3 * 1024));
            var solutionAnalyzer = new RecordingSolutionAnalyzer((_, context) => new SolutionAnalysisResult
            {
                Documents = [CreateSolutionDocument(context, "Big.cs", "Big")]
            });
            var store = new InMemoryGraphStore();
            var logger = new RecordingLogger<IndexingPipeline>();
            IndexingPipeline Pipeline() => new(
                store,
                [new TrackingCSharpExtractor()],
                Options.Create(new IndexingOptions
                {
                    MaxParallelFiles = 1, MaxFileSizeKb = 1, TrustedDotnetRepositories = "folder:Big"
                }),
                new LocalFileSystem(),
                logger,
                solutionAnalyzer);

            await Pipeline().IndexProjectAsync(
                "Big", rootPath, repositoryToolingIdentity: "folder:Big", ct: CancellationToken.None);
            logger.Messages.ShouldContain("Found 1 files, 1 changed");
            (await store.GetFileHashRecordsAsync("Big"))["Big.cs"].ExtractionVersion.ShouldEndWith(";oversize>1KB");

            logger.Messages.Clear();
            await Pipeline().IndexProjectAsync(
                "Big", rootPath, repositoryToolingIdentity: "folder:Big", ct: CancellationToken.None);
            logger.Messages.ShouldContain("Found 1 files, 0 changed");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void IndexingOptions_SwiftHasItsOwnHigherSizeLimit()
    {
        var options = new IndexingOptions();
        options.MaxFileSizeKbFor(".swift").ShouldBe(2048);
        options.MaxFileSizeKbFor(".c").ShouldBe(512);
        options.MaxFileSizeKbFor(".SWIFT").ShouldBe(2048);
    }

    [Fact]
    public async Task IndexProjectAsync_ExtractionVersionBumpForOneFileType_KeepsCrossFileEdgesWithUnchangedFiles()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-extraction-crossfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "caller.ca"), "caller");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "target.tg"), "target");
            var store = new InMemoryGraphStore();
            var extractor = new VersionedExtractor(".ca", ".tg");

            await CreatePipeline(store, extractor).IndexProjectAsync("CrossRepo", rootPath, ct: CancellationToken.None);
            extractor.CallCount.ShouldBe(2);

            extractor.Versions[".ca"] = "2";
            await CreatePipeline(store, extractor).IndexProjectAsync("CrossRepo", rootPath, ct: CancellationToken.None);

            // Only the bumped file type was re-extracted.
            extractor.CallCount.ShouldBe(3);
            var caller = (await store.FindNodeByQualifiedNameAsync("CrossRepo", "CrossRepo.caller"))!;
            var target = (await store.FindNodeByQualifiedNameAsync("CrossRepo", "CrossRepo.target"))!;
            // Re-extracted -> unchanged: the unresolved call resolves against the persisted node.
            (await store.FindEdgesBySourceAsync(caller.Id, EdgeType.CALLS))
                .ShouldContain(edge => edge.TargetId == target.Id);
            // Unchanged -> re-extracted: the incoming edge survives the file-slice replacement.
            (await store.FindEdgesBySourceAsync(target.Id, EdgeType.CALLS))
                .ShouldContain(edge => edge.TargetId == caller.Id);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// Emits one class node per file whose name is the file name. A "caller" file calls the
    /// "target" node (bare call resolved by the pipeline); a "target" file calls "caller" directly.
    /// </summary>
    private sealed class VersionedExtractor : ICodeExtractor
    {
        private int _callCount;

        public VersionedExtractor(params string[] extensions)
        {
            if (extensions.Length == 0)
                extensions = [".py"];
            SupportedExtensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            foreach (var extension in extensions)
                Versions[extension] = "1";
        }

        public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CallCount => _callCount;
        public IReadOnlySet<string> SupportedExtensions { get; }

        public string GetExtractionVersion(string extension) => Versions[extension];

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            var name = Path.GetFileNameWithoutExtension(filePath);
            var qualifiedName = $"{context.ProjectName}.{name}";
            return Task.FromResult(new ExtractionResult
            {
                Nodes =
                [
                    new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = NodeLabel.Class,
                        Name = name,
                        QualifiedName = qualifiedName,
                        FilePath = Path.GetRelativePath(context.RootPath, filePath),
                        StartLine = 1,
                        EndLine = 1
                    }
                ],
                Edges = content == "target"
                    ? [new PendingEdge(qualifiedName, $"{context.ProjectName}.caller", EdgeType.CALLS)]
                    : [],
                UnresolvedCalls = content == "caller"
                    ? [new UnresolvedCall(qualifiedName, "target", null, 0.9, CallReceiverKind.Bare)]
                    : []
            });
        }
    }

    private static IndexingPipeline CreatePipeline(InMemoryGraphStore store, ICodeExtractor extractor) =>
        new(
            store,
            [extractor],
            Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance);

    private sealed class TestMetadataExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".sh", ".c", ".h"], StringComparer.OrdinalIgnoreCase);

        public async Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            var extension = Path.GetExtension(filePath);
            var delay = extension.ToLowerInvariant() switch
            {
                ".sh" => 1,
                ".c" => 50,
                ".h" => 50,
                _ => 1
            };

            await Task.Delay(delay, ct);

            var metadata = extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
                ? new ProjectMetadata("Bash", null)
                : new ProjectMetadata("C", null);

            return new ExtractionResult
            {
                Metadata = metadata
            };
        }
    }

    private sealed class TestSupportMetadataExtractor(DotnetSupportInfo support) : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase);

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ExtractionResult
            {
                Metadata = new ProjectMetadata("C#", ".NET", support)
            });
        }
    }

    private sealed class MixedRustPythonMetadataExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".rs", ".py"], StringComparer.OrdinalIgnoreCase);

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            var metadata = Path.GetExtension(filePath).Equals(".rs", StringComparison.OrdinalIgnoreCase)
                ? new ProjectMetadata("Rust", "Cargo")
                : new ProjectMetadata("Python", null);

            return Task.FromResult(new ExtractionResult
            {
                Metadata = metadata
            });
        }
    }

    private sealed class ThrowingRustAnalyzer(Exception failure) : IRustAnalyzer
    {
        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
            string cargoManifestPath,
            ExtractorContext context,
            CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<ExtractionResult>>(failure);
    }

    private sealed class SucceedingRustAnalyzer : IRustAnalyzer
    {
        public const int AnalysisDelayMs = 20;

        public async Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
            string cargoManifestPath,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            await Task.Delay(AnalysisDelayMs, ct);
            return
            [
                new ExtractionResult
                {
                    Nodes =
                    [
                        new GraphNode
                        {
                            Project = context.ProjectName,
                            Label = NodeLabel.Function,
                            Name = "semantic_alpha",
                            QualifiedName = $"{context.ProjectName}:semantic_alpha",
                            FilePath = "crates/alpha/src/lib.rs"
                        }
                    ],
                    ProcessedFiles = ["crates/alpha/src/lib.rs", "crates/beta/src/lib.rs"],
                    Metadata = new ProjectMetadata("Rust", "Cargo")
                }
            ];
        }
    }

    private sealed class PartiallyEnumeratingRustAnalyzer : IRustAnalyzer
    {
        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
            string cargoManifestPath,
            ExtractorContext context,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExtractionResult>>(
            [
                new ExtractionResult
                {
                    Nodes = new ThrowAfterFirstReadOnlyList<GraphNode>(new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = NodeLabel.Function,
                        Name = "partial-semantic-node",
                        QualifiedName = $"{context.ProjectName}:partial-semantic-node",
                        FilePath = "src/lib.rs"
                    }),
                    Metadata = new ProjectMetadata("Rust", "Cargo")
                }
            ]);
    }

    private sealed class ThrowAfterFirstReadOnlyList<T>(T first) : IReadOnlyList<T>
    {
        public int Count => 2;
        public T this[int index] => index switch
        {
            0 => first,
            _ => throw new InvalidOperationException("Synthetic lazy result failure.")
        };

        public IEnumerator<T> GetEnumerator()
        {
            yield return first;
            throw new InvalidOperationException("Synthetic lazy result failure.");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private class NodeProducingExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".py", ".rs"], StringComparer.OrdinalIgnoreCase);

        public virtual Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            var relPath = Path.GetRelativePath(context.RootPath, filePath);
            var extension = Path.GetExtension(filePath);
            var language = extension.Equals(".rs", StringComparison.OrdinalIgnoreCase)
                ? "Rust"
                : "Python";

            return Task.FromResult(new ExtractionResult
            {
                Nodes =
                [
                    new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = NodeLabel.Class,
                        Name = Path.GetFileNameWithoutExtension(filePath),
                        QualifiedName = $"{context.ProjectName}.{Path.GetFileNameWithoutExtension(filePath)}",
                        FilePath = relPath,
                        StartLine = 1,
                        EndLine = 1
                    }
                ],
                Metadata = new ProjectMetadata(language, null)
            });
        }
    }

    private sealed class ThrowingExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>([".py"]);

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("synthetic extraction failure");
    }

    private sealed class CountingExtractor : NodeProducingExtractor
    {
        public int CallCount { get; private set; }

        public override Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            CallCount++;
            return base.ExtractAsync(filePath, content, context, ct);
        }
    }

    private sealed class AlwaysFailingReplacementExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>([".cg"]);

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("synthetic extraction failure");
    }

    private sealed class ReportedFailureReplacementExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>([".cg"]);

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default) =>
            Task.FromResult(ExtractionResult.Failure("production extractor reported a parse failure"));
    }

    private sealed class ReplacementGraphExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>([".cg"]);

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            var relPath = Path.GetRelativePath(context.RootPath, filePath).Replace('\\', '/');
            var fileQn = $"{context.ProjectName}:{relPath}";
            if (content == "caller")
            {
                return Task.FromResult(new ExtractionResult
                {
                    Nodes = [Node(NodeLabel.Method, "Caller.Run", "Caller.Run")],
                    Edges =
                    [
                        new PendingEdge(fileQn, $"{context.ProjectName}.Caller.Run", EdgeType.DEFINES_METHOD),
                        new PendingEdge($"{context.ProjectName}.Caller.Run", $"{context.ProjectName}.OldController.OldAction", EdgeType.CALLS)
                    ]
                });
            }

            if (content == "target")
            {
                return Task.FromResult(new ExtractionResult
                {
                    Nodes = [Node(NodeLabel.Method, "Target.Run", "Target.Run")],
                    Edges = [new PendingEdge(fileQn, $"{context.ProjectName}.Target.Run", EdgeType.DEFINES_METHOD)]
                });
            }

            var isOld = content == "old";
            var className = isOld ? "OldController" : "NewController";
            var methodName = isOld ? "OldAction" : "NewAction";
            var route = isOld ? "/old" : "/new";
            var classQn = $"{context.ProjectName}.{className}";
            var methodQn = $"{classQn}.{methodName}";
            var routeQn = $"route:GET:{route}";
            var edges = new List<PendingEdge>
            {
                new(fileQn, classQn, EdgeType.DEFINES),
                new(classQn, methodQn, EdgeType.DEFINES_METHOD),
                new(methodQn, routeQn, EdgeType.HANDLES)
            };
            if (isOld)
                edges.Add(new PendingEdge(methodQn, $"{context.ProjectName}.Target.Run", EdgeType.CALLS));

            return Task.FromResult(new ExtractionResult
            {
                Nodes =
                [
                    Node(NodeLabel.Class, className, className),
                    Node(NodeLabel.Method, methodName, $"{className}.{methodName}"),
                    new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = NodeLabel.Route,
                        Name = route,
                        QualifiedName = routeQn,
                        FilePath = relPath
                    }
                ],
                Edges = edges
            });

            GraphNode Node(NodeLabel label, string name, string suffix) => new()
            {
                Project = context.ProjectName,
                Label = label,
                Name = name,
                QualifiedName = $"{context.ProjectName}.{suffix}",
                FilePath = relPath
            };
        }
    }

    private sealed class ConcurrencyTrackingExtractor : NodeProducingExtractor
    {
        private readonly object sync = new();
        private int active;
        private int maxConcurrent;

        public int MaxConcurrent => maxConcurrent;

        public override async Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            var current = Interlocked.Increment(ref active);
            lock (sync)
                maxConcurrent = Math.Max(maxConcurrent, current);
            try
            {
                await Task.Delay(40, ct);
                return await base.ExtractAsync(filePath, content, context, ct);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private static SolutionDocumentAnalysis CreateSolutionDocument(
        ExtractorContext context,
        string fileName,
        string className,
        bool includeSelfEdge = false)
    {
        var qualifiedName = $"{context.ProjectName}.{className}";
        return new SolutionDocumentAnalysis(
            Path.Combine(context.RootPath, fileName),
            new ExtractionResult
            {
                Nodes =
                [
                    new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = NodeLabel.Class,
                        Name = className,
                        QualifiedName = qualifiedName,
                        FilePath = fileName
                    }
                ],
                Edges = includeSelfEdge
                    ? [new PendingEdge(qualifiedName, qualifiedName, EdgeType.CALLS)]
                    : []
            });
    }

    private sealed class TrackingCSharpExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase);
        public List<string> CalledFiles { get; } = [];

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            CalledFiles.Add(filePath);
            var className = Path.GetFileNameWithoutExtension(filePath);
            return Task.FromResult(new ExtractionResult
            {
                Nodes =
                [
                    new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = NodeLabel.Class,
                        Name = className,
                        QualifiedName = $"{context.ProjectName}.{className}",
                        FilePath = Path.GetRelativePath(context.RootPath, filePath)
                    }
                ]
            });
        }
    }

    private sealed class RecordingSolutionAnalyzer(
        Func<string, ExtractorContext, SolutionAnalysisResult>? analyze = null) : ISolutionAnalyzer
    {
        public string Version { get; init; } = "1";
        public string ExtractionVersion => Version;

        public string? CalledSolutionPath { get; private set; }
        public List<string> CalledSolutionPaths { get; } = [];
        public RepositoryToolingTrust? ObservedTrust { get; private set; }

        public Task<SolutionAnalysisResult> AnalyzeSolutionAsync(
            string solutionPath,
            ExtractorContext context,
            CancellationToken ct)
        {
            CalledSolutionPath = solutionPath;
            CalledSolutionPaths.Add(solutionPath);
            ObservedTrust = context.RepositoryToolingTrust;
            return Task.FromResult(analyze?.Invoke(solutionPath, context) ?? new SolutionAnalysisResult());
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public List<(LogLevel Level, IReadOnlyDictionary<string, object?> Properties)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Entries.Add((logLevel, state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>()));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
