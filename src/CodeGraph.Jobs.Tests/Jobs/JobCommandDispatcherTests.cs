using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.WikiRag;

namespace CodeGraph.Jobs.Tests.Jobs;

public class JobCommandDispatcherTests
{
    [Fact]
    public void NormalizeArgsJson_UsesTypedDefaults()
    {
        var dispatcher = CreateDispatcher();
        using var args = JsonDocument.Parse("""{"repo":"Orders.Api"}""");

        var normalized = dispatcher.NormalizeArgsJson(JobTypes.ProcessBatchAnalysis, args.RootElement);

        normalized.ShouldContain("Orders.Api");
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesDiscoverJob()
    {
        var dispatcher = CreateDispatcher();
        var result = await dispatcher.ExecuteAsync(
            JobTypes.Discover,
            JsonSerializer.Serialize(new DiscoverRequest
            {
                NamePattern = "orders"
            }),
            "job:execution-1");

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("published");
    }

    [Fact]
    public async Task ExecuteAsync_ReusesCallerExecutionKeyToDeduplicatePublication()
    {
        var indexerClient = new RecordingIndexerClient();
        var dispatcher = CreateDispatcher(indexerClient: indexerClient);
        var args = JsonSerializer.Serialize(new DiscoverRequest { NamePattern = "orders" });

        await dispatcher.ExecuteAsync(JobTypes.Discover, args, "job:stable-execution");
        await dispatcher.ExecuteAsync(JobTypes.Discover, args, "job:stable-execution");

        indexerClient.SubmissionKeys.ShouldBe(["job:stable-execution", "job:stable-execution"]);
        indexerClient.PublicationCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesAssistantRetentionCleanupJob()
    {
        var cleanupService = new RecordingAssistantRetentionCleanupService
        {
            Result = new AssistantRetentionCleanupResult(1, 1, 1, 1, 1, 1)
        };
        var dispatcher = CreateDispatcher(cleanupService);

        var result = await dispatcher.ExecuteAsync(JobTypes.AssistantRetentionCleanup, "{}");

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("6 rows");
        cleanupService.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesPruneWorkMessagesJob()
    {
        var retention = new RecordingWorkMessageRetentionService { Result = new MessageRetentionResult(2, 3, 4) };
        var dispatcher = CreateDispatcher(workMessageRetention: retention);

        var result = await dispatcher.ExecuteAsync(JobTypes.PruneWorkMessages, "{}");

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("9 rows");
        retention.Calls.ShouldBe(1);
        dispatcher.GetSupportedJobTypes().ShouldContain(JobTypes.PruneWorkMessages);
        dispatcher.NormalizeArgsJson(JobTypes.PruneWorkMessages, null).ShouldBe("{}");
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesDetectCommunitiesThroughIndexerClient()
    {
        var indexerClient = new RecordingIndexerClient();
        var dispatcher = CreateDispatcher(indexerClient: indexerClient);

        var result = await dispatcher.ExecuteAsync(JobTypes.DetectCommunities, "{}");

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Queued indexer run");
        indexerClient.DetectCommunitiesCalls.ShouldBe(1);
    }

    internal static JobCommandDispatcher CreateDispatcherWith(RecordingWorkSessionPruner pruner, CodeGraph.Models.Work.WorkSessionOptions sessionOptions)
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
            new IngestConventionEmbeddingsJob(new FakeConventionEmbeddingService()),
            new PruneWorkSessionsJob(pruner, Microsoft.Extensions.Options.Options.Create(sessionOptions)),
            new PruneWorkMessagesJob(new RecordingWorkMessageRetentionService()));
    }

    private static JobCommandDispatcher CreateDispatcher(
        RecordingAssistantRetentionCleanupService? cleanupService = null,
        RecordingIndexerClient? indexerClient = null,
        RecordingWorkMessageRetentionService? workMessageRetention = null)
    {
        indexerClient ??= new RecordingIndexerClient
        {
            NextAcceptedResponse = new IndexerAcceptedResponse("queued", "Queued discovery; published work.", 100, "/api/indexer/runs/100")
        };
        return new JobCommandDispatcher(
            new DiscoverRepositoriesJob(indexerClient, NullLogger<DiscoverRepositoriesJob>.Instance),
            new ReIndexAllRepositoriesJob(indexerClient),
            new ProcessBatchAnalysisJob(indexerClient),
            new LinkAndDetectJob(indexerClient),
            new DetectCommunitiesJob(indexerClient),
            new RegenerateMcpDocsJob(new RecordingMcpDocService()),
            new AssistantRetentionCleanupJob(cleanupService ?? new RecordingAssistantRetentionCleanupService()),
            new IngestConventionEmbeddingsJob(new FakeConventionEmbeddingService()),
            new PruneWorkSessionsJob(new RecordingWorkSessionPruner(), Microsoft.Extensions.Options.Options.Create(new CodeGraph.Models.Work.WorkSessionOptions())),
            new PruneWorkMessagesJob(workMessageRetention ?? new RecordingWorkMessageRetentionService()));
    }

    private sealed class FakeConventionEmbeddingService : IConventionEmbeddingService
    {
        public Task<int> IngestAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ReindexPageAsync(long pageId, bool deleted, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ConventionSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConventionSearchResult>>([]);
    }
}
