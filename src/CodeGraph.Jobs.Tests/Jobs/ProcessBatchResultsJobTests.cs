using Shouldly;
using CodeGraph.Jobs.Jobs;

namespace CodeGraph.Jobs.Tests.Jobs;

public class ProcessBatchResultsJobTests
{
    [Fact]
    public async Task ForwardsRepoArgumentToBatchService()
    {
        var batchService = new RecordingBatchAnalysisService();
        var job = new TestProcessBatchResultsJob(batchService);

        await job.InvokeAsync(new StartJob
        {
            Args = new Dictionary<string, string>
            {
                ["repo"] = "TC.OrdersApi"
            }
        });

        batchService.ProcessCompletedCalls.ShouldBe(1);
        batchService.ProcessedRepo.ShouldBe("TC.OrdersApi");
    }

    [Fact]
    public async Task MissingRepoArgument_ProcessesAllBatches()
    {
        var batchService = new RecordingBatchAnalysisService();
        var job = new TestProcessBatchResultsJob(batchService);

        await job.InvokeAsync(new StartJob { Args = [] });

        batchService.ProcessCompletedCalls.ShouldBe(1);
        batchService.ProcessedRepo.ShouldBeNull();
    }
}
