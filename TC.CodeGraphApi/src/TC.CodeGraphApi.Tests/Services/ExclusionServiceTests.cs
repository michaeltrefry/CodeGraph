using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Services;
using TC.CodeGraphApi.Tests.Extractors;

namespace TC.CodeGraphApi.Tests.Services;

public class ExclusionServiceTests
{
    private readonly InMemoryGraphStore _store;
    private readonly ExclusionService _service;

    public ExclusionServiceTests()
    {
        _store = new InMemoryGraphStore();
        _service = new ExclusionService(_store, NullLogger<ExclusionService>.Instance);
    }

    // ── GetExclusionTypeAsync ────────────────────────────────────────

    [Fact]
    public async Task NoRules_ReturnsNull()
    {
        var result = await _service.GetExclusionTypeAsync("TC.SomeApi", "services/some-group");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task RepoRule_MatchesExactName()
    {
        await _service.CreateRuleAsync("repository", "TC.SomeApi", "complete", null, "test");

        var result = await _service.GetExclusionTypeAsync("TC.SomeApi", "services/some-group");
        result.ShouldBe("complete");
    }

    [Fact]
    public async Task RepoRule_IsCaseInsensitive()
    {
        await _service.CreateRuleAsync("repository", "tc.someapi", "no_analysis", null, "test");

        var result = await _service.GetExclusionTypeAsync("TC.SomeApi", null);
        result.ShouldBe("no_analysis");
    }

    [Fact]
    public async Task RepoRule_DoesNotMatchDifferentRepo()
    {
        await _service.CreateRuleAsync("repository", "TC.SomeApi", "complete", null, "test");

        var result = await _service.GetExclusionTypeAsync("TC.OtherApi", "services/some-group");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GroupRule_MatchesFirstSegment()
    {
        await _service.CreateRuleAsync("group", "old-svn-repos", "complete", null, "test");

        var result = await _service.GetExclusionTypeAsync("my-project", "old-svn-repos/sub");
        result.ShouldBe("complete");
    }

    [Fact]
    public async Task GroupRule_MatchesMiddleSegment()
    {
        await _service.CreateRuleAsync("group", "utilities", "no_analysis", null, "test");

        var result = await _service.GetExclusionTypeAsync("my-project", "parent/utilities/child");
        result.ShouldBe("no_analysis");
    }

    [Fact]
    public async Task GroupRule_IsCaseInsensitive()
    {
        await _service.CreateRuleAsync("group", "Utilities", "complete", null, "test");

        var result = await _service.GetExclusionTypeAsync("my-project", "parent/utilities");
        result.ShouldBe("complete");
    }

    [Fact]
    public async Task GroupRule_DoesNotMatchPartialSegment()
    {
        await _service.CreateRuleAsync("group", "util", "complete", null, "test");

        // "utilities" contains "util" but is not an exact segment match
        var result = await _service.GetExclusionTypeAsync("my-project", "utilities/sub");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GroupRule_NullGroup_ReturnsNull()
    {
        await _service.CreateRuleAsync("group", "utilities", "complete", null, "test");

        var result = await _service.GetExclusionTypeAsync("my-project", null);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task RepoRule_TakesPrecedenceOverGroupRule()
    {
        await _service.CreateRuleAsync("group", "services", "complete", null, "test");
        await _service.CreateRuleAsync("repository", "TC.ImportantApi", "no_analysis", null, "test");

        // Repo is in excluded group, but has its own repo-level rule
        var result = await _service.GetExclusionTypeAsync("TC.ImportantApi", "services/important");
        result.ShouldBe("no_analysis");
    }

    // ── CRUD ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAndListRules()
    {
        await _service.CreateRuleAsync("group", "old-svn", "complete", "Legacy repos", "admin");
        await _service.CreateRuleAsync("repository", "TC.TestApi", "no_analysis", null, "admin");

        var rules = await _service.ListRulesAsync();
        rules.Count.ShouldBe(2);
    }

    [Fact]
    public async Task UpdateRule_ChangesExclusionType()
    {
        var created = await _service.CreateRuleAsync("group", "utilities", "complete", "Old", "admin");

        var updated = await _service.UpdateRuleAsync(created.Id, "no_analysis", "Changed");
        updated.ShouldNotBeNull();
        updated.ExclusionType.ShouldBe("no_analysis");
        updated.Reason.ShouldBe("Changed");

        // Verify the change is reflected in lookups
        var exclusionType = await _service.GetExclusionTypeAsync("my-project", "utilities");
        exclusionType.ShouldBe("no_analysis");
    }

    [Fact]
    public async Task UpdateRule_NonExistentId_ReturnsNull()
    {
        var result = await _service.UpdateRuleAsync(999, "complete", null);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteRule_RemovesFromLookup()
    {
        var created = await _service.CreateRuleAsync("group", "utilities", "complete", null, "admin");

        var deleted = await _service.DeleteRuleAsync(created.Id);
        deleted.ShouldBeTrue();

        var exclusionType = await _service.GetExclusionTypeAsync("my-project", "utilities");
        exclusionType.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteRule_NonExistentId_ReturnsFalse()
    {
        var result = await _service.DeleteRuleAsync(999);
        result.ShouldBeFalse();
    }

    // ── Cache invalidation ───────────────────────────────────────────

    [Fact]
    public async Task CacheInvalidatedOnCreate()
    {
        // Prime the cache
        var before = await _service.GetExclusionTypeAsync("my-project", "utilities");
        before.ShouldBeNull();

        // Create a rule — should invalidate cache
        await _service.CreateRuleAsync("group", "utilities", "complete", null, "admin");

        var after = await _service.GetExclusionTypeAsync("my-project", "utilities");
        after.ShouldBe("complete");
    }

    [Fact]
    public async Task CacheInvalidatedOnDelete()
    {
        var created = await _service.CreateRuleAsync("group", "utilities", "complete", null, "admin");

        // Prime the cache
        var before = await _service.GetExclusionTypeAsync("my-project", "utilities");
        before.ShouldBe("complete");

        // Delete — should invalidate cache
        await _service.DeleteRuleAsync(created.Id);

        var after = await _service.GetExclusionTypeAsync("my-project", "utilities");
        after.ShouldBeNull();
    }

    // ── SeedFromConfigAsync ──────────────────────────────────────────

    [Fact]
    public async Task SeedFromConfig_InsertsGroupRules()
    {
        await _service.SeedFromConfigAsync(["old-svn-repos", "utilities", "mysql.utilities"]);

        var rules = await _service.ListRulesAsync();
        rules.Count.ShouldBe(3);
        rules.ShouldAllBe(r => r.TargetType == "group");
        rules.ShouldAllBe(r => r.ExclusionType == "complete");
        rules.ShouldAllBe(r => r.CreatedBy == "system");
    }

    [Fact]
    public async Task SeedFromConfig_SkipsIfRulesAlreadyExist()
    {
        await _service.CreateRuleAsync("repository", "TC.SomeApi", "complete", null, "admin");

        await _service.SeedFromConfigAsync(["old-svn-repos", "utilities"]);

        var rules = await _service.ListRulesAsync();
        rules.Count.ShouldBe(1); // Only the manually created rule
    }

    [Fact]
    public async Task SeedFromConfig_EmptyList_NoOp()
    {
        await _service.SeedFromConfigAsync([]);

        var rules = await _service.ListRulesAsync();
        rules.Count.ShouldBe(0);
    }

    // ── GetSecretFilePathsAsync ──────────────────────────────────────

    [Fact]
    public async Task SecretFilePaths_ReturnsDistinctSecretFiles()
    {
        // Add some security findings — mix of secrets and other categories
        await _store.UpsertSecurityFindingsBatchAsync("TestRepo", [
            new SecurityFindingEntity
            {
                Project = "TestRepo", Category = "secret", Severity = "critical",
                Title = "AWS Key", Description = "Found AWS key",
                FilePath = "src/Config.cs", ComputedAt = DateTime.UtcNow
            },
            new SecurityFindingEntity
            {
                Project = "TestRepo", Category = "secret", Severity = "high",
                Title = "Connection String", Description = "Found conn string",
                FilePath = "src/Config.cs", ComputedAt = DateTime.UtcNow
            },
            new SecurityFindingEntity
            {
                Project = "TestRepo", Category = "secret", Severity = "critical",
                Title = "Private Key", Description = "Found private key",
                FilePath = "src/Keys/private.cs", ComputedAt = DateTime.UtcNow
            },
            new SecurityFindingEntity
            {
                Project = "TestRepo", Category = "vulnerability", Severity = "high",
                Title = "Vulnerable NuGet", Description = "Old package",
                FilePath = "src/Service.cs", ComputedAt = DateTime.UtcNow
            },
            new SecurityFindingEntity
            {
                Project = "TestRepo", Category = "attack_surface", Severity = "medium",
                Title = "SQL Injection", Description = "String interpolation",
                FilePath = "src/Data.cs", ComputedAt = DateTime.UtcNow
            }
        ]);

        var secretPaths = await _service.GetSecretFilePathsAsync("TestRepo");

        secretPaths.Count.ShouldBe(2);
        secretPaths.ShouldContain("src/Config.cs");
        secretPaths.ShouldContain("src/Keys/private.cs");
        secretPaths.ShouldNotContain("src/Service.cs");
        secretPaths.ShouldNotContain("src/Data.cs");
    }

    [Fact]
    public async Task SecretFilePaths_EmptyForProjectWithNoFindings()
    {
        var secretPaths = await _service.GetSecretFilePathsAsync("NonExistent");
        secretPaths.Count.ShouldBe(0);
    }

    [Fact]
    public async Task SecretFilePaths_IgnoresNullFilePaths()
    {
        await _store.UpsertSecurityFindingsBatchAsync("TestRepo", [
            new SecurityFindingEntity
            {
                Project = "TestRepo", Category = "secret", Severity = "critical",
                Title = "JWT Secret", Description = "Found JWT",
                FilePath = null, ComputedAt = DateTime.UtcNow
            }
        ]);

        var secretPaths = await _service.GetSecretFilePathsAsync("TestRepo");
        secretPaths.Count.ShouldBe(0);
    }

    [Fact]
    public async Task SecretFilePaths_OnlyReturnsForRequestedProject()
    {
        await _store.UpsertSecurityFindingsBatchAsync("RepoA", [
            new SecurityFindingEntity
            {
                Project = "RepoA", Category = "secret", Severity = "critical",
                Title = "AWS Key", Description = "Found",
                FilePath = "src/config.cs", ComputedAt = DateTime.UtcNow
            }
        ]);
        await _store.UpsertSecurityFindingsBatchAsync("RepoB", [
            new SecurityFindingEntity
            {
                Project = "RepoB", Category = "secret", Severity = "critical",
                Title = "Private Key", Description = "Found",
                FilePath = "src/key.cs", ComputedAt = DateTime.UtcNow
            }
        ]);

        var pathsA = await _service.GetSecretFilePathsAsync("RepoA");
        pathsA.Count.ShouldBe(1);
        pathsA.ShouldContain("src/config.cs");

        var pathsB = await _service.GetSecretFilePathsAsync("RepoB");
        pathsB.Count.ShouldBe(1);
        pathsB.ShouldContain("src/key.cs");
    }
}
