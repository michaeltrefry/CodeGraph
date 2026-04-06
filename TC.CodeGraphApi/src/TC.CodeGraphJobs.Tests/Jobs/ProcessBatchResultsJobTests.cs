using Shouldly;
using TC.JobUtilities;

namespace TC.CodeGraphJobs.Tests.Jobs;

public class ProcessBatchResultsJobTests
{
    [Fact]
    public async Task ForwardsRepoArgumentToBatchService()
    {
        var serviceBus = new RecordingServiceBus();
        var batchService = new RecordingBatchAnalysisService();
        var job = new TestProcessBatchResultsJob(serviceBus, batchService);

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
        var serviceBus = new RecordingServiceBus();
        var batchService = new RecordingBatchAnalysisService();
        var job = new TestProcessBatchResultsJob(serviceBus, batchService);

        await job.InvokeAsync(new StartJob { Args = [] });

        batchService.ProcessCompletedCalls.ShouldBe(1);
        batchService.ProcessedRepo.ShouldBeNull();
    }
}
