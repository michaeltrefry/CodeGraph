using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Services;

namespace CodeGraph.Tests.Services;

public class FolderRepoProviderTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-folder-provider-{Guid.NewGuid():N}");

    public FolderRepoProviderTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    [Fact]
    public async Task DiscoverProjectsAsync_FindsNestedRepositories_AndPreservesGroups()
    {
        CreateRepo("flat-repo");
        CreateRepo(Path.Combine("group-a", "nested-repo"));

        var provider = CreateProvider();

        var results = await provider.DiscoverProjectsAsync();

        results.Select(r => r.Name).ShouldContain("flat-repo");
        results.Select(r => r.Name).ShouldContain("nested-repo");

        var nested = results.Single(r => r.Name == "nested-repo");
        nested.PathWithNamespace.ShouldBe("group-a/nested-repo");
        nested.HttpUrlToRepo.ShouldBe(Path.Combine(_rootPath, "group-a", "nested-repo"));
    }

    [Fact]
    public async Task EnsureLocalAsync_ResolvesUniqueNestedRepositoryByName()
    {
        var repoPath = CreateRepo(Path.Combine("group-b", "orders-api"));
        var provider = CreateProvider();

        var resolved = await provider.EnsureLocalAsync("orders-api", null, null);

        resolved.ShouldBe(repoPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private FolderRepoProvider CreateProvider()
    {
        return new FolderRepoProvider(
            Options.Create(new RepositorySourceOptions
            {
                Provider = RepositorySourceProvider.Folder,
                Folder = new FolderSourceOptions { RootPath = _rootPath }
            }),
            new NoOpExclusionService(),
            NullLogger<FolderRepoProvider>.Instance);
    }

    private string CreateRepo(string relativePath)
    {
        var repoPath = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        return repoPath;
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
}
