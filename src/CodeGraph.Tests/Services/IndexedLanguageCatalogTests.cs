using CodeGraph.Api;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Which file languages CodeGraph indexes, as the API host's registered extractors say (sc-26137): the catalog the MCP file tools answer <c>languageIndexed</c>/<c>language</c> from.
/// </summary>
public class IndexedLanguageCatalogTests
{
    /// <summary>The catalog the API host resolves for <see cref="CodeGraphMcpServer"/>.</summary>
    internal static IndexedLanguageCatalog ApiCatalog()
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, Configuration());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IndexedLanguageCatalog>();
    }

    [Theory]
    // Indexed languages: tree-sitter specs, Roslyn, the TypeScript extractor, SQL, Terraform.
    [InlineData("Sources/App/Model.swift", true, "Swift")]
    [InlineData("src/Orders/OrderService.cs", true, "C#")]
    [InlineData("src/app/app.component.ts", true, "TypeScript")]
    [InlineData("crates/core/src/lib.rs", true, "Rust")]
    [InlineData("native/bridge.cpp", true, "C++")]
    [InlineData("tools/build.py", true, "Python")]
    [InlineData("db/schema.sql", true, "SQL")]
    [InlineData("infra/main.tf", true, "Terraform")]
    // Discovery matches extensions case-sensitively: these are never indexed.
    [InlineData("SRC/UPPER.SWIFT", false, "Swift")]
    [InlineData("src/Legacy.CS", false, "C#")]
    [InlineData("tools/x.PY", false, "Python")]
    // Source languages CodeGraph does not index.
    [InlineData("src/app/View.tsx", true, "TypeScript")]
    [InlineData("Sources/Legacy/Bridge.m", false, "Objective-C")]
    [InlineData("Sources/Legacy/Bridge.mm", false, "Objective-C++")]
    [InlineData("app/src/main/Main.kt", false, "Kotlin")]
    [InlineData("build.gradle.kts", false, "Kotlin")]
    [InlineData("core/Model.scala", false, "Scala")]
    [InlineData("lib/main.dart", false, "Dart")]
    [InlineData("web/index.js", true, "JavaScript")]
    [InlineData("web/View.jsx", true, "JavaScript")]
    // Non-code files: indexed (nothing to miss), no language.
    [InlineData("README.md", true, null)]
    [InlineData("config/settings.json", true, null)]
    [InlineData("Assets/icon.png", true, null)]
    [InlineData("Makefile", true, null)]
    public void ApiCatalog_DescribesIndexedUnindexedAndNonCodeFiles(string path, bool indexed, string? language)
    {
        ApiCatalog().Describe(path).ShouldBe(new FileLanguage(indexed, language));
    }

    [Fact]
    public void WithoutExtractors_EverySourceLanguageIsUnindexed()
    {
        var none = new IndexedLanguageCatalog([]);

        none.Describe("Model.swift").ShouldBe(new FileLanguage(false, "Swift"));
        none.Describe("README.md").ShouldBe(new FileLanguage(true, null));
    }

    /// <summary>
    /// Indexing a tree with mixed-case extensions through the API host's extractor extensions:
    /// every source file the catalog calls indexed is discovered by the default branch, accepted
    /// by the overlay (<see cref="IndexingPipeline.IsIndexablePath"/>), extracted when a change
    /// list names it, and versioned with its extractor; every one it calls unindexed is none of
    /// these (sc-26138: the extractors' own sets ignore case, discovery does not).
    /// </summary>
    [Fact]
    public async Task IndexedSourceFiles_AreExactlyTheFilesDiscoveryIndexes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-language-parity-{Guid.NewGuid():N}");
        string[] files =
        [
            "Sources/A.swift", "Sources/B.SWIFT", "Sources/C.Swift", "src/a.py", "src/b.PY", "src/c.cs", "src/d.CS",
            "src/e.ts", "src/f.tsx", "src/g.TS", "db/h.sql", "db/i.SQL", "infra/j.tf", "Sources/k.m", "Sources/l.mm",
            "app/m.kt", "web/n.js", "native/o.h", "native/p.H", "native/q.cpp"
        ];
        try
        {
            foreach (var file in files)
            {
                var path = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x\n");
            }

            var services = new ServiceCollection();
            Startup.ConfigureServices(services, Configuration());
            using var provider = services.BuildServiceProvider();
            // The API host's extension sets, without running Roslyn or the sidecar.
            ICodeExtractor[] extractors = provider.GetServices<ICodeExtractor>()
                .Select(extractor => (ICodeExtractor)new ExtensionsOnlyExtractor(extractor.SupportedExtensions))
                .ToArray();
            var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(store, extractors,
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);
            await pipeline.IndexRepositoryAsync("Parity", root, ct: CancellationToken.None);
            var discovered = (await store.GetFileHashesAsync("Parity")).Keys.ToHashSet(StringComparer.Ordinal);
            // A replacing run records the hash of every discovered file, extracted or not, so it
            // shows discovery itself rather than discovery filtered by extraction dispatch.
            var replaceStore = new InMemoryGraphStore();
            await new IndexingPipeline(replaceStore, extractors,
                    Options.Create(new IndexingOptions { MaxParallelFiles = 1 }), new LocalFileSystem(),
                    NullLogger<IndexingPipeline>.Instance)
                .IndexRepositoryAsync("Replace", root, replaceExistingGraph: true, ct: CancellationToken.None);
            var discoveredByReplace = (await replaceStore.GetFileHashesAsync("Replace")).Keys.ToHashSet(StringComparer.Ordinal);

            // A change list (incremental and overlay runs) names files without discovery's filter:
            // extraction dispatch decides which of them an extractor reads.
            var extracted = new System.Collections.Concurrent.ConcurrentBag<string>();
            ICodeExtractor[] recording = extractors
                .Select(extractor => (ICodeExtractor)new ExtensionsOnlyExtractor(extractor.SupportedExtensions, extracted))
                .ToArray();
            var changeListPipeline = new IndexingPipeline(new InMemoryGraphStore(), recording,
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);
            await changeListPipeline.IndexRepositoryAsync("ChangeList", root, changedFilesOnly: files, ct: CancellationToken.None);
            var extractedFiles = extracted
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .ToHashSet(StringComparer.Ordinal);

            var tree = RepositoryTree.FromFiles(files);
            var versions = new ExtractionVersionCatalog(extractors);
            var catalog = new IndexedLanguageCatalog(extractors);
            foreach (var file in files)
            {
                var language = catalog.Describe(file);
                language.Language.ShouldNotBeNull(file);
                language.Indexed.ShouldBe(discovered.Contains(file), file);
                discoveredByReplace.Contains(file).ShouldBe(language.Indexed, file);
                pipeline.IsIndexablePath(file, tree).ShouldBe(language.Indexed, file);
                extractedFiles.Contains(file).ShouldBe(language.Indexed, file);
                versions.ForPath(file).Contains(nameof(ExtensionsOnlyExtractor)).ShouldBe(language.Indexed, file);
            }

            discovered.ShouldContain("Sources/A.swift");
            discovered.ShouldNotContain("Sources/B.SWIFT");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ExtensionsOnlyExtractor(
        IReadOnlySet<string> extensions,
        System.Collections.Concurrent.ConcurrentBag<string>? extracted = null) : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = extensions;

        public Task<ExtractionResult> ExtractAsync(string filePath, string content, ExtractorContext context,
            CancellationToken ct = default)
        {
            extracted?.Add(filePath);
            return Task.FromResult(new ExtractionResult());
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeGraph.sln")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("CodeGraph.sln not found above the test binaries.");
    }

    [Fact]
    public void EveryIndexedExtension_HasALanguageName()
    {
        var catalog = ApiCatalog();

        catalog.IndexedExtensions.ShouldNotBeEmpty();
        foreach (var extension in catalog.IndexedExtensions)
            catalog.Describe($"file{extension}").Language.ShouldNotBeNull($"no language name for indexed extension {extension}");
    }

    [Fact]
    public void ApiHost_GivesTheMcpServerTheRegisteredCatalog()
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, Configuration());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var server = scope.ServiceProvider.GetRequiredService<CodeGraphMcpServer>();

        typeof(CodeGraphMcpServer)
            .GetField("indexedLanguages", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(server)
            .ShouldBeSameAs(provider.GetRequiredService<IndexedLanguageCatalog>());
    }

    [Fact]
    public void ApiAndIndexerHosts_RegisterTheSameExtractorsAndAnalyzers()
    {
        // The API answers languageIndexed from its own registrations; the indexer host is what indexes.
        var api = new ServiceCollection();
        Startup.ConfigureServices(api, Configuration());
        var indexer = new ServiceCollection();
        CodeGraph.Indexer.Host.Startup.ConfigureServices(indexer, Configuration());

        Implementations(api).ShouldBe(Implementations(indexer), ignoreOrder: true);
    }

    private static string[] Implementations(IServiceCollection services)
    {
        Type[] indexingTypes = [typeof(ICodeExtractor), typeof(ISolutionAnalyzer), typeof(ITypeScriptAnalyzer), typeof(IRustAnalyzer)];
        return services
            .Where(descriptor => indexingTypes.Contains(descriptor.ServiceType))
            .Select(descriptor => $"{descriptor.ServiceType.Name}={descriptor.ImplementationType?.FullName}")
            .ToArray();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
                ["CodeGraph:RepositorySource:Provider"] = "Folder",
                ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;",
                ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
            })
            .Build();
}
