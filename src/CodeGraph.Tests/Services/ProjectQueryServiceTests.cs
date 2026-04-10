using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class ProjectQueryServiceTests
{
    [Fact]
    public async Task GetHealthAsync_MapsRepositoryVitalityAndOrdersHotspotsByConcernScore()
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity
        {
            Name = "TestProject",
            LocalPath = "/tmp/testproject",
            Language = "C#",
            Framework = ".NET"
        });

        await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
        {
            Project = "TestProject",
            DotnetProject = null,
            OverallHealth = 6.8,
            TotalFiles = 4,
            HotspotCount = 2,
            AlertCount = 0,
            HistoryMaturity = "Growing",
            HasSufficientHistoryForTrends = true,
            ActivityStatus = "Slowing",
            FirefightingStatus = "Moderate",
            MonthlyCommitCounts = """[{"month":"2025-10","commitCount":8},{"month":"2025-11","commitCount":5}]""",
            VelocityLast6Months = 12,
            VelocityPrior6Months = 20,
            VelocityChangePercent = -40,
            DormantMonths12m = 1,
            MaxInactiveStreakMonths = 1,
            FirefightingCommits90d = 2,
            FirefightingCommits365d = 5,
            FirefightingRate90d = 0.18,
            FirefightingRate365d = 0.12,
            ComputedAt = new DateTime(2026, 4, 9, 0, 0, 0, DateTimeKind.Utc)
        });

        await store.UpsertFileMetricsBatchAsync("TestProject",
        [
            new FileMetricsEntity
            {
                Project = "TestProject",
                FilePath = "src/LowRiskHighConcern.cs",
                DotnetProject = "TestProject.Api",
                HealthScore = 4.8,
                RiskScore = 8,
                ConcernScore = 22,
                BugFixCommits365d = 1.5,
                BugFixRatio365d = 0.6,
                RecurringChurnScore = 0.7,
                ComputedAt = DateTime.UtcNow
            },
            new FileMetricsEntity
            {
                Project = "TestProject",
                FilePath = "src/HighRiskLowConcern.cs",
                DotnetProject = "TestProject.Api",
                HealthScore = 3.5,
                RiskScore = 20,
                ConcernScore = 10,
                BugFixCommits365d = 0.1,
                BugFixRatio365d = 0.05,
                RecurringChurnScore = 0.1,
                ComputedAt = DateTime.UtcNow
            }
        ]);

        var service = new ProjectQueryService(
            store,
            Options.Create(new RepositorySourceOptions()));

        var response = await service.GetHealthAsync("TestProject");

        response.ShouldNotBeNull();
        response.RepositoryVitality.ShouldNotBeNull();
        response.RepositoryVitality.HistoryMaturity.ShouldBe(Models.Responses.HistoryMaturity.Growing);
        response.RepositoryVitality.ActivityStatus.ShouldBe("Slowing");
        response.RepositoryVitality.FirefightingStatus.ShouldBe("Moderate");
        response.RepositoryVitality.MonthlyCommits.Count.ShouldBe(2);
        response.TopHotspots.Count.ShouldBe(2);
        response.TopHotspots[0].FilePath.ShouldBe("src/LowRiskHighConcern.cs");
        response.TopHotspots[0].ConcernScore.ShouldBe(22);
    }
}
