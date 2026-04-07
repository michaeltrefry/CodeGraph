using CodeGraph.Api;
using CodeGraph.Data;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ApiStartupInitializationTests
{
    [Fact]
    public async Task InitializeAsync_AppliesMigrationsBeforeSeedingExclusions()
    {
        var lifecycle = new List<string>();
        var migrationRunner = new RecordingMigrationRunner(lifecycle);
        var wikiSectionSeedService = new RecordingWikiSectionSeedService(lifecycle);
        var exclusionService = new RecordingExclusionService(lifecycle);
        var services = new ServiceCollection();

        services.AddSingleton<IMigrationRunner>(migrationRunner);
        services.AddSingleton<IWikiSectionSeedService>(wikiSectionSeedService);
        services.AddSingleton<IExclusionService>(exclusionService);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment
        {
            ContentRootPath = "/repo"
        });
        services.AddSingleton(Options.Create(new CodeGraphStorageOptions
        {
            Neo4jMigrationsPath = "Migrations"
        }));
        services.AddSingleton(Options.Create(new RepositorySourceOptions
        {
            ExcludedGroups = ["foo", "bar"]
        }));

        using var serviceProvider = services.BuildServiceProvider();

        await Startup.InitializeAsync(serviceProvider);

        migrationRunner.AppliedPaths.ShouldBe([Path.GetFullPath(Path.Combine("/repo", "Migrations"))]);
        exclusionService.SeededGroups.ShouldBe([["foo", "bar"]]);
        lifecycle.ShouldBe(["migrate", "wiki", "seed"]);
    }

    private sealed class RecordingMigrationRunner(List<string> lifecycle) : IMigrationRunner
    {
        public List<string> AppliedPaths { get; } = [];

        public Task ApplyMigrationsAsync(string migrationsPath)
        {
            lifecycle.Add("migrate");
            AppliedPaths.Add(migrationsPath);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWikiSectionSeedService(List<string> lifecycle) : IWikiSectionSeedService
    {
        public int Calls { get; private set; }

        public Task EnsureDefaultSectionsAsync()
        {
            Calls++;
            lifecycle.Add("wiki");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExclusionService(List<string> lifecycle) : IExclusionService
    {
        public List<IReadOnlyList<string>> SeededGroups { get; } = [];

        public Task<string?> GetExclusionTypeAsync(string repoName, string? sourceGroup) => Task.FromResult<string?>(null);
        public Task<HashSet<string>> GetSecretFilePathsAsync(string project) => Task.FromResult(new HashSet<string>());
        public Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync() => Task.FromResult<IReadOnlyList<ExclusionRuleEntity>>([]);
        public Task<ExclusionRuleEntity> CreateRuleAsync(string targetType, string targetValue, string exclusionType, string? reason, string createdBy) => throw new NotSupportedException();
        public Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(long id) => throw new NotSupportedException();

        public Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups)
        {
            lifecycle.Add("seed");
            SeededGroups.Add(excludedGroups.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "CodeGraph.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
