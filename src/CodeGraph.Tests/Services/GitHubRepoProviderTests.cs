using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Tests.Services;

public class GitHubRepoProviderTests
{
    [Fact]
    public async Task DiscoverProjectsAsync_CombinesAuthenticatedUserUsersAndOrganizations_AndDeduplicates()
    {
        using var handler = new StubHandler();
        handler.EnqueueRepositories(Repo(1, "personal", "michaeltrefry"), Repo(2, "shared", "SceneWorks"));
        handler.EnqueueRepositories(Repo(3, "other-user-repo", "octocat"));
        handler.EnqueueRepositories(Repo(2, "shared", "SceneWorks"), Repo(4, "studio", "SceneWorks"));

        var provider = CreateProvider(
            handler,
            organization: "",
            userAccounts: "octocat",
            organizations: "SceneWorks");

        var projects = await provider.DiscoverProjectsAsync();

        projects.Select(project => project.Id).ShouldBe([1, 2, 3, 4]);
        handler.RequestUris.ShouldContain(uri => uri.Contains("/user/repos?", StringComparison.Ordinal));
        handler.RequestUris.ShouldContain(uri => uri.Contains("/users/octocat/repos?", StringComparison.Ordinal));
        handler.RequestUris.ShouldContain(uri => uri.Contains("/orgs/SceneWorks/repos?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverProjectsAsync_PreservesLegacyOrganizationOnlyBehavior()
    {
        using var handler = new StubHandler();
        handler.EnqueueRepositories(Repo(1, "org-repo", "acme"));

        var provider = CreateProvider(handler);

        var projects = await provider.DiscoverProjectsAsync();

        projects.Single().PathWithNamespace.ShouldBe("acme/org-repo");
        handler.RequestUris.Single().ShouldContain("/orgs/acme/repos?");
        handler.RequestUris.ShouldNotContain(uri => uri.Contains("/user/repos?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverProjectsAsync_UsesAuthenticatedUserWhenNoOwnersAreConfigured()
    {
        using var handler = new StubHandler();
        handler.EnqueueRepositories(Repo(1, "personal", "michaeltrefry"));
        var provider = CreateProvider(handler, organization: "");

        var projects = await provider.DiscoverProjectsAsync();

        projects.Single().PathWithNamespace.ShouldBe("michaeltrefry/personal");
        handler.RequestUris.Single().ShouldContain("/user/repos?");
    }

    [Fact]
    public async Task DiscoverProjectsAsync_ParsesMultipleExplicitScopes_AndCanDisableAuthenticatedUser()
    {
        using var handler = new StubHandler();
        handler.EnqueueRepositories(Repo(1, "public-repo", "octocat"));
        handler.EnqueueRepositories(Repo(2, "automation", "hubot"));
        handler.EnqueueRepositories(Repo(3, "org-repo", "SceneWorks"));
        handler.EnqueueRepositories(Repo(4, "other-org-repo", "AnotherOrg"));

        var provider = CreateProvider(
            handler,
            organization: "",
            userAccounts: "octocat, hubot",
            organizations: "SceneWorks, AnotherOrg",
            includeAuthenticatedUser: false);

        var projects = await provider.DiscoverProjectsAsync();

        projects.Count.ShouldBe(4);
        handler.RequestUris.ShouldNotContain(uri => uri.Contains("/user/repos?", StringComparison.Ordinal));
        handler.RequestUris.ShouldContain(uri => uri.Contains("/users/hubot/repos?", StringComparison.Ordinal));
        handler.RequestUris.ShouldContain(uri => uri.Contains("/orgs/AnotherOrg/repos?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverProjectsAsync_PaginatesEachScope()
    {
        using var handler = new StubHandler();
        handler.EnqueueRepositories(Enumerable.Range(1, 100)
            .Select(id => Repo(id, $"repo-{id}", "acme"))
            .ToArray());
        handler.EnqueueRepositories(Repo(101, "repo-101", "acme"));

        var provider = CreateProvider(handler);

        var projects = await provider.DiscoverProjectsAsync();

        projects.Count.ShouldBe(101);
        handler.RequestUris.ShouldContain(uri => uri.Contains("page=1", StringComparison.Ordinal));
        handler.RequestUris.ShouldContain(uri => uri.Contains("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverProjectsAsync_SkipsArchivedAndExcludedRepositories()
    {
        using var handler = new StubHandler();
        handler.EnqueueRepositories(
            Repo(1, "kept", "acme"),
            Repo(2, "archived", "acme", archived: true),
            Repo(3, "excluded", "acme"));

        var provider = CreateProvider(
            handler,
            exclusionService: new TestExclusionService("excluded"));

        var projects = await provider.DiscoverProjectsAsync();

        projects.Select(project => project.Name).ShouldBe(["kept"]);
    }

    [Fact]
    public async Task SearchProjectsAsync_SearchesAcrossCombinedDiscoverySet()
    {
        using var handler = new StubHandler();
        handler.EnqueueRepositories(Repo(1, "personal", "michaeltrefry"));
        handler.EnqueueRepositories(Repo(2, "galleries", "SceneWorks"));

        var provider = CreateProvider(handler, organization: "", organizations: "SceneWorks");

        var results = await provider.SearchProjectsAsync("sceneworks/galleries");

        results.Select(project => project.Name).ShouldBe(["galleries"]);
        handler.RequestUris.ShouldNotContain(uri => uri.Contains("/search/repositories", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveRepoUrlAsync_UsesNormalizedNameMatchAcrossConfiguredScopes()
    {
        using var handler = new StubHandler();
        handler.EnqueueRepositories(Repo(1, "personal", "michaeltrefry"));
        handler.EnqueueRepositories(Repo(2, "date-and-rate", "SceneWorks"));

        var provider = CreateProvider(handler, organization: "", organizations: "SceneWorks");

        var resolved = await provider.ResolveRepoUrlAsync("DateAndRate", null);

        resolved.ShouldBe("https://github.com/SceneWorks/date-and-rate.git");
    }

    [Fact]
    public async Task DiscoverProjectsAsync_ReportsConfiguredScopeWhenGitHubRejectsIt()
    {
        using var handler = new StubHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var provider = CreateProvider(handler, organization: "SceneWorks");

        var exception = await Should.ThrowAsync<HttpRequestException>(
            () => provider.DiscoverProjectsAsync());

        exception.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        exception.Message.ShouldContain("organization 'SceneWorks'");
        exception.Message.ShouldContain("Verify the owner name and token access");
    }

    [Fact]
    public async Task DiscoverProjectsAsync_RequiresPersonalAccessToken()
    {
        using var handler = new StubHandler();
        var provider = CreateProvider(handler, personalAccessToken: "");

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => provider.DiscoverProjectsAsync());

        exception.Message.ShouldContain("PersonalAccessToken");
    }

    private static GitHubRepoProvider CreateProvider(
        HttpMessageHandler handler,
        string organization = "acme",
        string userAccounts = "",
        string organizations = "",
        bool includeAuthenticatedUser = true,
        string personalAccessToken = "test-token",
        IExclusionService? exclusionService = null)
    {
        return new GitHubRepoProvider(
            Options.Create(new RepositorySourceOptions
            {
                Provider = RepositorySourceProvider.GitHub,
                ReposCachePath = Path.Combine(Path.GetTempPath(), $"codegraph-cache-{Guid.NewGuid():N}"),
                GitHub = new GitHubSourceOptions
                {
                    BaseUrl = "https://api.github.com",
                    PersonalAccessToken = personalAccessToken,
                    IncludeAuthenticatedUser = includeAuthenticatedUser,
                    UserAccounts = userAccounts,
                    Organizations = organizations,
                    Organization = organization
                }
            }),
            new HttpClient(handler),
            exclusionService ?? new TestExclusionService(),
            NullLogger<GitHubRepoProvider>.Instance);
    }

    private static string Repo(int id, string name, string owner, bool archived = false) =>
        JsonSerializer.Serialize(new
        {
            id,
            name,
            full_name = $"{owner}/{name}",
            clone_url = $"https://github.com/{owner}/{name}.git",
            default_branch = "main",
            archived,
            updated_at = "2026-04-07T12:00:00Z"
        });

    private sealed class TestExclusionService(params string[] excludedRepositories) : IExclusionService
    {
        public Task<string?> GetExclusionTypeAsync(string repoName, string? sourceGroup) =>
            Task.FromResult<string?>(excludedRepositories.Contains(repoName, StringComparer.OrdinalIgnoreCase)
                ? "complete"
                : null);

        public Task<HashSet<string>> GetSecretFilePathsAsync(string project) =>
            Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync() =>
            Task.FromResult<IReadOnlyList<ExclusionRuleEntity>>([]);

        public Task<ExclusionRuleEntity> CreateRuleAsync(string targetType, string targetValue, string exclusionType, string? reason, string createdBy) =>
            throw new NotSupportedException();

        public Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason) =>
            throw new NotSupportedException();

        public Task<bool> DeleteRuleAsync(long id) =>
            throw new NotSupportedException();

        public Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups) =>
            Task.CompletedTask;
    }

    private sealed class StubHandler : HttpMessageHandler, IDisposable
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<string> RequestUris { get; } = [];

        public void EnqueueJson(string json)
        {
            Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        public void EnqueueRepositories(params string[] repositories) =>
            EnqueueJson($"[{string.Join(',', repositories)}]");

        public void Enqueue(HttpResponseMessage response)
        {
            _responses.Enqueue(response);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? "");
            if (_responses.Count == 0)
                throw new InvalidOperationException($"No stubbed response left for {request.Method} {request.RequestUri}");

            return Task.FromResult(_responses.Dequeue());
        }

        public new void Dispose()
        {
            while (_responses.Count > 0)
                _responses.Dequeue().Dispose();
        }
    }
}
