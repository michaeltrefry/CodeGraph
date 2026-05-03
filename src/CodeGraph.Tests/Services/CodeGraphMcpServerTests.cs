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
