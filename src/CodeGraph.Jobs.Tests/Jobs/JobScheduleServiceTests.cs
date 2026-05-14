using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Models.Requests;
using CodeGraph.Services.WikiRag;

namespace CodeGraph.Jobs.Tests.Jobs;

public class JobScheduleServiceTests
{
    [Fact]
    public async Task CreateAsync_ComputesNextRunAndNormalizesArgs()
    {
        var store = new InMemoryJobScheduleStore();
        var service = new JobScheduleService(store, CreateDispatcher(), NullLogger<JobScheduleService>.Instance);

        var created = await service.CreateAsync(new CreateJobScheduleRequest
        {
            Name = "Batch Poll",
            JobType = JobTypes.ProcessBatchAnalysis,
            CronExpression = "0 */6 * * *",
            TimeZoneId = "UTC",
            Args = JsonDocument.Parse("""{"repo":"Orders.Api"}""").RootElement
        });

        created.Id.ShouldBeGreaterThan(0);
        created.JobType.ShouldBe(JobTypes.ProcessBatchAnalysis);
        created.Args.GetProperty("repo").GetString().ShouldBe("Orders.Api");
        created.NextRunUtc.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task RunNowAsync_ThrowsWhenScheduleAlreadyLeased()
    {
        var store = new InMemoryJobScheduleStore();
        var service = new JobScheduleService(store, CreateDispatcher(), NullLogger<JobScheduleService>.Instance);
        var created = await service.CreateAsync(new CreateJobScheduleRequest
        {
            Name = "Busy schedule",
            JobType = JobTypes.ReIndexAll,
            CronExpression = "0 0 * * *",
            TimeZoneId = "UTC"
        });

        await store.TryAcquireScheduleAsync(created.Id, DateTime.UtcNow, "other-owner", TimeSpan.FromMinutes(15));

        await Should.ThrowAsync<InvalidOperationException>(() => service.RunNowAsync(created.Id));
    }

    private static JobCommandDispatcher CreateDispatcher()
    {
        var indexerClient = new RecordingIndexerClient();
        return new JobCommandDispatcher(
            new DiscoverRepositoriesJob(indexerClient, NullLogger<DiscoverRepositoriesJob>.Instance),
            new ReIndexAllRepositoriesJob(indexerClient),
            new ProcessBatchAnalysisJob(indexerClient),
            new LinkAndDetectJob(indexerClient),
            new DetectCommunitiesJob(indexerClient),
            new RegenerateMcpDocsJob(new RecordingMcpDocService()),
            new AssistantRetentionCleanupJob(new RecordingAssistantRetentionCleanupService()),
            new IngestConventionEmbeddingsJob(new FakeConventionEmbeddingService()));
    }

    private sealed class FakeConventionEmbeddingService : IConventionEmbeddingService
    {
        public Task<int> IngestAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ReindexPageAsync(long pageId, bool deleted, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ConventionSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConventionSearchResult>>([]);
    }
}
