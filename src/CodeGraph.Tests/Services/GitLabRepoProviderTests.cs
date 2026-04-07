using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Tests.Services;

public class GitLabRepoProviderTests
{
    [Fact]
    public async Task ResolveRepoUrlAsync_UsesExactSearchMatch_WhenMessageUrlIsMissing()
    {
        using var handler = new StubHandler();
        handler.EnqueueJson("""
            [
              {
                "id": 1,
                "name": "ai-toolkit",
                "path_with_namespace": "team/ai-toolkit",
                "http_url_to_repo": "https://gitlab.example.com/team/ai-toolkit.git",
                "default_branch": "main",
                "last_activity_at": "2026-04-07T12:00:00Z"
              }
            ]
            """);

        var provider = CreateProvider(handler);

        var resolved = await provider.ResolveRepoUrlAsync("ai-toolkit", null);

        resolved.ShouldBe("https://gitlab.example.com/team/ai-toolkit.git");
    }

    [Fact]
    public async Task ResolveRepoUrlAsync_FallsBackToDiscovery_WhenSearchMissesExactMatch()
    {
        using var handler = new StubHandler();
        handler.EnqueueJson("""
            [
              {
                "id": 2,
                "name": "ai-toolkit-docs",
                "path_with_namespace": "team/ai-toolkit-docs",
                "http_url_to_repo": "https://gitlab.example.com/team/ai-toolkit-docs.git",
                "default_branch": "main",
                "last_activity_at": "2026-04-07T12:00:00Z"
              }
            ]
            """);
        var discoveryResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [
                  {
                    "id": 1,
                    "name": "ai-toolkit",
                    "path_with_namespace": "team/ai-toolkit",
                    "http_url_to_repo": "https://gitlab.example.com/team/ai-toolkit.git",
                    "default_branch": "main",
                    "last_activity_at": "2026-04-07T12:00:00Z"
                  }
                ]
                """, Encoding.UTF8, "application/json")
        };
        discoveryResponse.Headers.Add("x-next-page", "1");
        handler.Enqueue(discoveryResponse);

        var provider = CreateProvider(handler);

        var resolved = await provider.ResolveRepoUrlAsync("ai-toolkit", null);

        resolved.ShouldBe("https://gitlab.example.com/team/ai-toolkit.git");
    }

    [Fact]
    public async Task ResolveRepoUrlAsync_FallsBackToDiscovery_WhenSearchIsForbidden()
    {
        using var handler = new StubHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var discoveryResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [
                  {
                    "id": 1,
                    "name": "ai-toolkit",
                    "path_with_namespace": "team/ai-toolkit",
                    "http_url_to_repo": "https://gitlab.example.com/team/ai-toolkit.git",
                    "default_branch": "main",
                    "last_activity_at": "2026-04-07T12:00:00Z"
                  }
                ]
                """, Encoding.UTF8, "application/json")
        };
        discoveryResponse.Headers.Add("x-next-page", "1");
        handler.Enqueue(discoveryResponse);

        var provider = CreateProvider(handler);

        var resolved = await provider.ResolveRepoUrlAsync("ai-toolkit", null);

        resolved.ShouldBe("https://gitlab.example.com/team/ai-toolkit.git");
    }

    private static GitLabRepoProvider CreateProvider(HttpMessageHandler handler)
    {
        return new GitLabRepoProvider(
            Options.Create(new RepositorySourceOptions
            {
                Provider = RepositorySourceProvider.GitLab,
                ReposCachePath = Path.Combine(Path.GetTempPath(), $"codegraph-cache-{Guid.NewGuid():N}"),
                GitLab = new GitLabSourceOptions
                {
                    BaseUrl = "https://gitlab.example.com",
                    PrivateToken = "test-token"
                }
            }),
            new HttpClient(handler),
            new NoOpExclusionService(),
            NullLogger<GitLabRepoProvider>.Instance);
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
