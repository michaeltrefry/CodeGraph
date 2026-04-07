using System.Net;
using System.Text;
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
    public async Task ResolveRepoUrlAsync_UsesExactSearchMatch_WhenMessageUrlIsMissing()
    {
        using var handler = new StubHandler();
        handler.EnqueueJson("""
            {
              "total_count": 2,
              "items": [
                {
                  "id": 1,
                  "name": "ai-toolkit",
                  "full_name": "acme/ai-toolkit",
                  "clone_url": "https://github.com/acme/ai-toolkit.git",
                  "default_branch": "main",
                  "archived": false,
                  "updated_at": "2026-04-07T12:00:00Z"
                },
                {
                  "id": 2,
                  "name": "ai-toolkit-docs",
                  "full_name": "acme/ai-toolkit-docs",
                  "clone_url": "https://github.com/acme/ai-toolkit-docs.git",
                  "default_branch": "main",
                  "archived": false,
                  "updated_at": "2026-04-07T12:00:00Z"
                }
              ]
            }
            """);

        var provider = CreateProvider(handler);

        var resolved = await provider.ResolveRepoUrlAsync("ai-toolkit", null);

        resolved.ShouldBe("https://github.com/acme/ai-toolkit.git");
    }

    [Fact]
    public async Task ResolveRepoUrlAsync_FallsBackToDiscovery_WhenSearchMissesExactMatch()
    {
        using var handler = new StubHandler();
        handler.EnqueueJson("""
            {
              "total_count": 1,
              "items": [
                {
                  "id": 2,
                  "name": "ai-toolkit-docs",
                  "full_name": "acme/ai-toolkit-docs",
                  "clone_url": "https://github.com/acme/ai-toolkit-docs.git",
                  "default_branch": "main",
                  "archived": false,
                  "updated_at": "2026-04-07T12:00:00Z"
                }
              ]
            }
            """);
        handler.EnqueueJson("""
            [
              {
                "id": 1,
                "name": "ai-toolkit",
                "full_name": "acme/ai-toolkit",
                "clone_url": "https://github.com/acme/ai-toolkit.git",
                "default_branch": "main",
                "archived": false,
                "updated_at": "2026-04-07T12:00:00Z"
              }
            ]
            """);

        var provider = CreateProvider(handler);

        var resolved = await provider.ResolveRepoUrlAsync("ai-toolkit", null);

        resolved.ShouldBe("https://github.com/acme/ai-toolkit.git");
    }

    [Fact]
    public async Task ResolveRepoUrlAsync_FallsBackToDiscovery_WhenSearchIsForbidden()
    {
        using var handler = new StubHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden));
        handler.EnqueueJson("""
            [
              {
                "id": 1,
                "name": "ai-toolkit",
                "full_name": "acme/ai-toolkit",
                "clone_url": "https://github.com/acme/ai-toolkit.git",
                "default_branch": "main",
                "archived": false,
                "updated_at": "2026-04-07T12:00:00Z"
              }
            ]
            """);

        var provider = CreateProvider(handler);

        var resolved = await provider.ResolveRepoUrlAsync("ai-toolkit", null);

        resolved.ShouldBe("https://github.com/acme/ai-toolkit.git");
    }

    [Fact]
    public async Task SearchProjectsAsync_UsesAccessibleRepoListing_WhenOrganizationIsBlank()
    {
        using var handler = new StubHandler();
        handler.EnqueueJson("""
            [
              {
                "id": 1,
                "name": "galleries",
                "full_name": "shared-org/galleries",
                "clone_url": "https://github.com/shared-org/galleries.git",
                "default_branch": "main",
                "archived": false,
                "updated_at": "2026-04-07T12:00:00Z"
              },
              {
                "id": 2,
                "name": "other-repo",
                "full_name": "shared-org/other-repo",
                "clone_url": "https://github.com/shared-org/other-repo.git",
                "default_branch": "main",
                "archived": false,
                "updated_at": "2026-04-07T12:00:00Z"
              }
            ]
            """);

        var provider = CreateProvider(handler, organization: "");

        var results = await provider.SearchProjectsAsync("galleries");

        results.Select(r => r.Name).ShouldBe(["galleries"]);
        handler.RequestUris.ShouldContain(uri => uri.Contains("/user/repos?", StringComparison.OrdinalIgnoreCase));
        handler.RequestUris.ShouldNotContain(uri => uri.Contains("/search/repositories", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveRepoUrlAsync_UsesNormalizedNameMatch_WhenRepoNameUsesDifferentSeparators()
    {
        using var handler = new StubHandler();
        handler.EnqueueJson("""
            [
              {
                "id": 1,
                "name": "date-and-rate",
                "full_name": "shared-org/date-and-rate",
                "clone_url": "https://github.com/shared-org/date-and-rate.git",
                "default_branch": "main",
                "archived": false,
                "updated_at": "2026-04-07T12:00:00Z"
              }
            ]
            """);

        var provider = CreateProvider(handler, organization: "");

        var resolved = await provider.ResolveRepoUrlAsync("DateAndRate", null);

        resolved.ShouldBe("https://github.com/shared-org/date-and-rate.git");
    }

    [Fact]
    public async Task ResolveRepoUrlAsync_UsesUniquePartialNormalizedMatch_WhenRepoHasSuffix()
    {
        using var handler = new StubHandler();
        handler.EnqueueJson("""
            [
              {
                "id": 1,
                "name": "date-and-rate-api",
                "full_name": "shared-org/date-and-rate-api",
                "clone_url": "https://github.com/shared-org/date-and-rate-api.git",
                "default_branch": "main",
                "archived": false,
                "updated_at": "2026-04-07T12:00:00Z"
              }
            ]
            """);
        handler.EnqueueJson("""
            [
              {
                "id": 1,
                "name": "date-and-rate-api",
                "full_name": "shared-org/date-and-rate-api",
                "clone_url": "https://github.com/shared-org/date-and-rate-api.git",
                "default_branch": "main",
                "archived": false,
                "updated_at": "2026-04-07T12:00:00Z"
              }
            ]
            """);

        var provider = CreateProvider(handler, organization: "");

        var resolved = await provider.ResolveRepoUrlAsync("DateAndRate", null);

        resolved.ShouldBe("https://github.com/shared-org/date-and-rate-api.git");
    }

    private static GitHubRepoProvider CreateProvider(HttpMessageHandler handler, string organization = "acme")
    {
        return new GitHubRepoProvider(
            Options.Create(new RepositorySourceOptions
            {
                Provider = RepositorySourceProvider.GitHub,
                ReposCachePath = Path.Combine(Path.GetTempPath(), $"codegraph-cache-{Guid.NewGuid():N}"),
                GitHub = new GitHubSourceOptions
                {
                    BaseUrl = "https://api.github.com",
                    PersonalAccessToken = "test-token",
                    Organization = organization
                }
            }),
            new HttpClient(handler),
            new NoOpExclusionService(),
            NullLogger<GitHubRepoProvider>.Instance);
    }

    private sealed class NoOpExclusionService : IExclusionService
    {
        public Task<string?> GetExclusionTypeAsync(string repoName, string? sourceGroup) =>
            Task.FromResult<string?>(null);

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
