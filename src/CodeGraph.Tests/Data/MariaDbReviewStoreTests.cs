using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbReviewStoreTests
{
    [Fact]
    public void MySqlReviewStore_ImplementsStandaloneReviewContract()
    {
        typeof(IReviewStore).IsAssignableFrom(typeof(MySqlReviewStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlReviewStore_RoundTripsDiagnosticsAndReviewsWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_review_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var runner = new MariaDbMigrationRunner(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var options = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(
                    builder.ConnectionString,
                    ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;

            await using var context = new CodeGraphDbContext(options);
            var store = new MySqlReviewStore(context);
            var now = DateTime.UtcNow;

            await store.UpsertProjectDiagnosticsBatchAsync("CodeGraph",
            [
                new ProjectDiagnosticEntity
                {
                    DotnetProject = "CodeGraph.Api",
                    Source = "roslyn",
                    DiagnosticKey = "CodeGraph.Api:warning:Widget.cs:10:CS0168",
                    DiagnosticId = "CS0168",
                    Severity = "warning",
                    Message = "Unused variable",
                    FilePath = "Widget.cs",
                    LineStart = 10,
                    ComputedAt = now
                },
                new ProjectDiagnosticEntity
                {
                    DotnetProject = "CodeGraph.Api",
                    Source = "roslyn",
                    DiagnosticKey = $"CodeGraph.Api:error:Widget.cs:5:CS1002:{new string('x', 400)}",
                    DiagnosticId = "CS1002",
                    Severity = "error",
                    Message = "Missing semicolon",
                    FilePath = "Widget.cs",
                    LineStart = 5,
                    ComputedAt = now
                }
            ]);

            var diagnostics = await store.GetProjectDiagnosticsAsync("CodeGraph", "CodeGraph.Api");
            diagnostics.Select(d => d.Severity).ShouldBe(["error", "warning"]);
            diagnostics.First().DiagnosticKey.Length.ShouldBeLessThanOrEqualTo(ProjectDiagnosticKey.MaxLength);

            await store.UpsertProjectDiagnosticsBatchAsync("CodeGraph",
            [
                new ProjectDiagnosticEntity
                {
                    DotnetProject = "CodeGraph.Api",
                    Source = "roslyn",
                    DiagnosticKey = "CodeGraph.Api:warning:Widget.cs:10:CS0168",
                    DiagnosticId = "CS0168",
                    Severity = "info",
                    Message = "Updated diagnostic",
                    FilePath = "Widget.cs",
                    LineStart = 10,
                    ComputedAt = now
                }
            ]);

            (await store.GetProjectDiagnosticsAsync("CodeGraph")).Count.ShouldBe(2);
            (await store.GetProjectDiagnosticsAsync("CodeGraph")).Last().Message.ShouldBe("Updated diagnostic");

            var projectRunId = await store.CreateProjectReviewRunAsync(new ProjectReviewRunEntity
            {
                Project = "CodeGraph",
                ProjectName = "CodeGraph.Api",
                ReviewedCommitSha = "abc123",
                Status = "queued",
                ReviewMode = "standard",
                PromptVersion = "v1",
                ModelUsed = "model",
                CreatedAt = now
            });

            await store.UpdateProjectReviewRunStatusAsync(projectRunId, "completed", "{\"ok\":true}", now, null);
            var projectRun = (await store.GetProjectReviewRunAsync(projectRunId))!;
            projectRun.Status.ShouldBe("completed");
            projectRun.StartedAt.ShouldNotBeNull();
            projectRun.CompletedAt.ShouldNotBeNull();
            (await store.GetLatestProjectReviewRunAsync("CodeGraph", "CodeGraph.Api"))!.Id.ShouldBe(projectRunId);

            await store.UpsertProjectReviewFindingsAsync(projectRunId,
            [
                new ProjectReviewFindingEntity
                {
                    Ordinal = 2,
                    Severity = "medium",
                    Category = "maintainability",
                    Title = "Second",
                    Explanation = "Later item",
                    Evidence = "Evidence",
                    FilePath = "Widget.cs",
                    SuggestedImprovement = "Improve it",
                    Confidence = "medium"
                },
                new ProjectReviewFindingEntity
                {
                    Ordinal = 1,
                    Severity = "high",
                    Category = "bug",
                    Title = "First",
                    Explanation = "Earlier item",
                    Evidence = "Evidence",
                    FilePath = "Widget.cs",
                    SuggestedImprovement = "Fix it",
                    Confidence = "high"
                }
            ]);

            (await store.GetProjectReviewFindingsAsync(projectRunId))
                .Select(f => f.Title)
                .ShouldBe(["First", "Second"]);

            await store.UpsertProjectReviewFindingsAsync(projectRunId, []);
            (await store.GetProjectReviewFindingsAsync(projectRunId)).ShouldBeEmpty();

            var repositoryRunId = await store.CreateRepositoryReviewRunAsync(new RepositoryReviewRunEntity
            {
                Repo = "CodeGraph",
                ReviewedCommitSha = "abc123",
                Status = "queued",
                ReviewMode = "full",
                PromptVersion = "v1",
                CreatedAt = now
            });

            await store.UpdateRepositoryReviewRunStatusAsync(repositoryRunId, "running");
            (await store.GetRepositoryReviewRunAsync(repositoryRunId))!.StartedAt.ShouldNotBeNull();
            (await store.GetLatestRepositoryReviewRunAsync("CodeGraph"))!.Id.ShouldBe(repositoryRunId);
            (await store.GetRepositoryReviewRunsByStatusAsync(["RUNNING"])).Single().Id.ShouldBe(repositoryRunId);

            await store.UpsertRepositoryReviewFindingsAsync(repositoryRunId,
            [
                new RepositoryReviewFindingEntity
                {
                    ProjectName = "CodeGraph.Api",
                    Ordinal = 1,
                    Severity = "medium",
                    Category = "test",
                    Title = "Finding",
                    Explanation = "Explanation",
                    Evidence = "Evidence",
                    FilePath = "Startup.cs",
                    SuggestedImprovement = "Add coverage",
                    Confidence = "medium"
                }
            ]);

            await store.UpsertRepositoryReviewProjectSectionsAsync(repositoryRunId,
            [
                new RepositoryReviewProjectSectionEntity
                {
                    ProjectName = "CodeGraph.Api",
                    Overview = "API review",
                    StrengthsJson = "[\"clear\"]",
                    ReviewedAreasJson = "[\"api\"]",
                    SkippedAreasJson = "[]",
                    FollowUpsJson = "[\"wire DI\"]",
                    ReusedFromBaseline = true
                }
            ]);

            (await store.GetRepositoryReviewFindingsAsync(repositoryRunId)).Single().Title.ShouldBe("Finding");
            (await store.GetRepositoryReviewProjectSectionsAsync(repositoryRunId)).Single().ReusedFromBaseline.ShouldBeTrue();
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = ""
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }
}
