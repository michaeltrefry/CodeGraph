using Shouldly;
using CodeGraph.Jobs.Jobs;

namespace CodeGraph.Jobs.Tests.Jobs;

public class ProcessBatchAnalysisJobTests
{
    [Fact]
    public async Task ForwardsRepoArgumentToBatchService()
    {
        var batchService = new RecordingBatchAnalysisService();
        var job = new ProcessBatchAnalysisJob(batchService);

        await job.ExecuteAsync(new ProcessBatchAnalysisJobRequest
        {
            Repo = "Orders.Api"
        });

        batchService.ProcessCompletedCalls.ShouldBe(1);
        batchService.ProcessedRepo.ShouldBe("Orders.Api");
    }

    [Fact]
    public async Task MissingRepoArgument_ProcessesAllBatches()
    {
        var batchService = new RecordingBatchAnalysisService();
        var job = new ProcessBatchAnalysisJob(batchService);

        await job.ExecuteAsync(new ProcessBatchAnalysisJobRequest());

        batchService.ProcessCompletedCalls.ShouldBe(1);
        batchService.ProcessedRepo.ShouldBeNull();
    }
}
