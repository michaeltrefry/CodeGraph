using CodeGraph.Api;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Extractors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Which file languages CodeGraph indexes, as the API host's registered extractors and analyzers
/// say (sc-26137): the catalog the MCP file tools answer <c>languageIndexed</c>/<c>language</c> from.
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
    // Indexed languages: tree-sitter specs, Roslyn, the TypeScript extractor and project analyzer, SQL, Terraform.
    [InlineData("Sources/App/Model.swift", true, "Swift")]
    [InlineData("src/Orders/OrderService.cs", true, "C#")]
    [InlineData("src/app/app.component.ts", true, "TypeScript")]
    [InlineData("src/app/View.tsx", true, "TypeScript")]
    [InlineData("crates/core/src/lib.rs", true, "Rust")]
    [InlineData("native/bridge.cpp", true, "C++")]
    [InlineData("tools/build.py", true, "Python")]
    [InlineData("db/schema.sql", true, "SQL")]
    [InlineData("infra/main.tf", true, "Terraform")]
    [InlineData("SRC/UPPER.SWIFT", true, "Swift")]
    // Source languages CodeGraph does not index.
    [InlineData("Sources/Legacy/Bridge.m", false, "Objective-C")]
    [InlineData("Sources/Legacy/Bridge.mm", false, "Objective-C++")]
    [InlineData("app/src/main/Main.kt", false, "Kotlin")]
    [InlineData("build.gradle.kts", false, "Kotlin")]
    [InlineData("core/Model.scala", false, "Scala")]
    [InlineData("lib/main.dart", false, "Dart")]
    [InlineData("web/index.js", false, "JavaScript")]
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
    public void IndexedExtensions_ComeFromTheRegisteredExtractorsAndAnalyzers()
    {
        // Without the TypeScript project analyzer nothing reads .tsx files.
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, Configuration());
        using var provider = services.BuildServiceProvider();
        var extractorsOnly = new IndexedLanguageCatalog(provider.GetServices<ICodeExtractor>());
        extractorsOnly.Describe("View.tsx").ShouldBe(new FileLanguage(false, "TypeScript"));
        extractorsOnly.Describe("Model.swift").ShouldBe(new FileLanguage(true, "Swift"));

        // No extractors registered: every source language is unindexed (fail closed).
        var none = new IndexedLanguageCatalog([]);
        none.Describe("Model.swift").ShouldBe(new FileLanguage(false, "Swift"));
        none.Describe("README.md").ShouldBe(new FileLanguage(true, null));
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
