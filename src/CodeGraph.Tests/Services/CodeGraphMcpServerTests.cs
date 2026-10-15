using System.Reflection;
using CodeGraph.Data;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class CodeGraphMcpServerTests
{
    [Fact]
    public async Task SearchProjects_ReturnsRepoUrlsAndSupportsPartialSearch()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "CodeGraph",
            RepoUrl = "https://github.com/example/codegraph.git",
            Language = "C#",
            Framework = "ASP.NET Web API",
            IndexedAt = new DateTime(2026, 5, 1, 12, 30, 0)
        });
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "DateAndRate",
            RepoUrl = "https://github.com/example/date-and-rate.git"
        });
        var sut = CreateServer(store);

        var markdown = await sut.SearchProjects("Graph");

        markdown.ShouldContain("## Indexed Projects (1)");
        markdown.ShouldContain("- **CodeGraph** [C#, ASP.NET Web API] (indexed: 2026-05-01 12:30)");
        markdown.ShouldContain("  Repo: https://github.com/example/codegraph.git");
        markdown.ShouldNotContain("DateAndRate");
    }

    /// <summary>
    /// sc-26138: a Rust workspace with a large Swift app was tagged [Rust, Cargo] only. Tags now
    /// add every other language with at least 10% of the non-blank LOC or at least 50 files.
    /// </summary>
    [Fact]
    public async Task SearchProjects_TagsOtherSignificantLanguagesFromLanguageStats()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "LensWorks",
            Language = "Rust",
            Framework = "Cargo",
            // Swift: under 10% of LOC but 294 files; TypeScript: 12% of LOC; Python: neither.
            Properties = """
                {"languageStats":{
                  "Rust":{"files":400,"locTotal":90000,"locNonBlank":80000,"nodeCount":1,"locShare":0.8},
                  "TypeScript":{"files":20,"locTotal":13000,"locNonBlank":12000,"nodeCount":1,"locShare":0.12},
                  "Swift":{"files":294,"locTotal":8000,"locNonBlank":7000,"nodeCount":1,"locShare":0.07},
                  "Python":{"files":3,"locTotal":1100,"locNonBlank":1000,"nodeCount":1,"locShare":0.01}
                },"primaryLanguageBasis":"locNonBlank"}
                """
        });
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "LensPlain", Language = "Rust" });
        var sut = CreateServer(store);

        var markdown = await sut.SearchProjects("Lens");

        markdown.ShouldContain("- **LensWorks** [Rust, TypeScript, Swift, Cargo]");
        markdown.ShouldContain("- **LensPlain** [Rust]");
#pragma warning disable CS0618
        (await sut.ListProjects()).ShouldContain("- **LensWorks** [Rust, TypeScript, Swift, Cargo]");
#pragma warning restore CS0618
    }

    [Fact]
    public async Task SearchProjects_SupportsWildcardSearch()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "CodeGraph",
            RepoUrl = "https://github.com/example/codegraph.git"
        });
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "CodeFlow",
            RepoUrl = "https://github.com/example/codeflow.git"
        });
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "DateAndRate",
            RepoUrl = "https://github.com/example/date-and-rate.git"
        });
        var sut = CreateServer(store);

        var markdown = await sut.SearchProjects("Code*");

        markdown.ShouldContain("## Indexed Projects (2)");
        markdown.ShouldContain("CodeGraph");
        markdown.ShouldContain("CodeFlow");
        markdown.ShouldNotContain("DateAndRate");
    }

    [Fact]
    public void ListProjects_IsMarkedObsolete()
    {
        var obsolete = typeof(CodeGraphMcpServer)
            .GetMethod(nameof(CodeGraphMcpServer.ListProjects))!
            .GetCustomAttribute<ObsoleteAttribute>();

        obsolete.ShouldNotBeNull();
        obsolete.Message.ShouldBe("Use search_projects instead.");
    }

    private static CodeGraphMcpServer CreateServer(IGraphStore store) =>
        new(
            null!,
            null!,
            store,
            null!,
            Options.Create(new RepositorySourceOptions()),
            null!,
            null!);
}
