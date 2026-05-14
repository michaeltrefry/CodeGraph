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
            }));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("published");
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
    public async Task ExecuteAsync_DispatchesDetectCommunitiesThroughIndexerClient()
    {
        var indexerClient = new RecordingIndexerClient();
        var dispatcher = CreateDispatcher(indexerClient: indexerClient);

        var result = await dispatcher.ExecuteAsync(JobTypes.DetectCommunities, "{}");

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Queued indexer run");
        indexerClient.DetectCommunitiesCalls.ShouldBe(1);
    }

    private static JobCommandDispatcher CreateDispatcher(
        RecordingAssistantRetentionCleanupService? cleanupService = null,
        RecordingIndexerClient? indexerClient = null)
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
