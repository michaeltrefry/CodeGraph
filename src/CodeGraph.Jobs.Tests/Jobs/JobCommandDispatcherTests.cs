using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

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

    private static JobCommandDispatcher CreateDispatcher(
        RecordingAssistantRetentionCleanupService? cleanupService = null)
    {
        var adminService = new RecordingAdminService
        {
            NextDiscoverResponse = new DiscoverResponse(3, 2, 1, 1, 0, ["Orders.Api"])
        };
        var batchService = new RecordingBatchAnalysisService();
        return new JobCommandDispatcher(
            new DiscoverRepositoriesJob(adminService, NullLogger<DiscoverRepositoriesJob>.Instance),
            new ReIndexAllRepositoriesJob(adminService),
            new ProcessBatchAnalysisJob(batchService),
            new LinkAndDetectJob(adminService),
            new DetectCommunitiesJob(adminService),
            new RegenerateMcpDocsJob(new RecordingMcpDocService()),
            new AssistantRetentionCleanupJob(cleanupService ?? new RecordingAssistantRetentionCleanupService()));
    }
}
