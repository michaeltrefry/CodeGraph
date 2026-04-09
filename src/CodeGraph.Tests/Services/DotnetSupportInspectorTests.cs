using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Metadata;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class DotnetSupportInspectorTests
{
    [Fact]
    public void InspectRepository_DetectsOutOfSupportSdkAndTargetFramework()
    {
        var rootPath = CreateTempRepo();

        try
        {
            File.WriteAllText(Path.Combine(rootPath, "global.json"), """
                {
                  "sdk": {
                    "version": "2.1.802"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(rootPath, "Legacy.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netcoreapp2.1</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var support = DotnetSupportInspector.InspectRepository(rootPath, new DateTime(2026, 4, 7, 0, 0, 0, DateTimeKind.Utc));

            support.ShouldNotBeNull();
            support.OverallStatus.ShouldBe("out_of_support");
            support.Sdk.ShouldNotBeNull();
            support.Sdk.Version.ShouldBe("2.1.802");
            support.Sdk.SupportStatus.ShouldBe("out_of_support");
            support.Sdk.SupportEndedOn.ShouldBe(new DateTime(2021, 8, 21, 0, 0, 0, DateTimeKind.Utc));
            support.TargetFrameworks.ShouldHaveSingleItem();
            support.TargetFrameworks[0].Moniker.ShouldBe("netcoreapp2.1");
            support.TargetFrameworks[0].DisplayName.ShouldBe(".NET Core 2.1");
            support.TargetFrameworks[0].SupportStatus.ShouldBe("out_of_support");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetDetailAsync_FallsBackToInspectingRepoPath_WhenStoredSupportMetadataIsMissing()
    {
        var rootPath = CreateTempRepo();

        try
        {
            File.WriteAllText(Path.Combine(rootPath, "global.json"), """
                {
                  "sdk": {
                    "version": "2.1.802"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(rootPath, "Legacy.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netcoreapp2.1</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var store = new InMemoryGraphStore();
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = "DynamoDb.Fluent",
                LocalPath = rootPath,
                Language = "C#",
                Framework = ".NET"
            });

            var service = new ProjectQueryService(
                store,
                Options.Create(new RepositorySourceOptions()));

            var detail = await service.GetDetailAsync("DynamoDb.Fluent");

            detail.ShouldNotBeNull();
            detail.DotnetSupport.ShouldNotBeNull();
            detail.DotnetSupport.OverallStatus.ShouldBe("out_of_support");
            detail.DotnetSupport.Sdk.ShouldNotBeNull();
            detail.DotnetSupport.Sdk.Version.ShouldBe("2.1.802");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetHealthAsync_AppliesDotnetSupportPenaltyToRepoHealth()
    {
        var rootPath = CreateTempRepo();

        try
        {
            File.WriteAllText(Path.Combine(rootPath, "global.json"), """
                {
                  "sdk": {
                    "version": "2.1.802"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(rootPath, "Legacy.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netcoreapp2.1</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var store = new InMemoryGraphStore();
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = "Legacy.Api",
                LocalPath = rootPath,
                Language = "C#",
                Framework = ".NET"
            });
            await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
            {
                Project = "Legacy.Api",
                OverallHealth = 6.4,
                TotalFiles = 12,
                HotspotCount = 2,
                AlertCount = 1,
                ComputedAt = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc)
            });

            var service = new ProjectQueryService(
                store,
                Options.Create(new RepositorySourceOptions()));

            var health = await service.GetHealthAsync("Legacy.Api");

            health.ShouldNotBeNull();
            health.DotnetSupport.ShouldNotBeNull();
            health.DotnetSupport.OverallStatus.ShouldBe("out_of_support");
            health.RepoHealth.ShouldNotBeNull();
            health.RepoHealth.BaseOverallHealth.ShouldBe(6.4);
            health.RepoHealth.ScorePenalty.ShouldBe(DotnetSupportHealthPolicy.OutOfSupportPenalty);
            health.RepoHealth.OverallHealth.ShouldBe(3.9);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetHealthAsync_ReturnsDotnetSupport_WhenVitalsSummaryIsMissing()
    {
        var rootPath = CreateTempRepo();

        try
        {
            File.WriteAllText(Path.Combine(rootPath, "global.json"), """
                {
                  "sdk": {
                    "version": "8.0.100"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(rootPath, "Modern.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var store = new InMemoryGraphStore();
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = "Modern.Api",
                LocalPath = rootPath,
                Language = "C#",
                Framework = ".NET"
            });

            var service = new ProjectQueryService(
                store,
                Options.Create(new RepositorySourceOptions()));

            var health = await service.GetHealthAsync("Modern.Api");

            health.ShouldNotBeNull();
            health.DotnetSupport.ShouldNotBeNull();
            health.DotnetSupport.OverallStatus.ShouldBe("supported");
            health.RepoHealth.ShouldBeNull();
            health.ProjectHealths.ShouldBeEmpty();
            health.TopHotspots.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static string CreateTempRepo()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-dotnet-support-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }
}
