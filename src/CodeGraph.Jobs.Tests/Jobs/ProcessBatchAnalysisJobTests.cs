using Shouldly;
using CodeGraph.Jobs.Jobs;

namespace CodeGraph.Jobs.Tests.Jobs;

public class ProcessBatchAnalysisJobTests
{
    [Fact]
    public async Task ForwardsRepoArgumentToBatchService()
    {
        var indexerClient = new RecordingIndexerClient();
        var job = new ProcessBatchAnalysisJob(indexerClient);

        await job.ExecuteAsync(new ProcessBatchAnalysisJobRequest
        {
            Repo = "Orders.Api"
        });

        indexerClient.ProcessBatchAnalysisCalls.ShouldBe(1);
        indexerClient.LastBatchRepo.ShouldBe("Orders.Api");
    }

    [Fact]
    public async Task MissingRepoArgument_ProcessesAllBatches()
    {
        var indexerClient = new RecordingIndexerClient();
        var job = new ProcessBatchAnalysisJob(indexerClient);

        await job.ExecuteAsync(new ProcessBatchAnalysisJobRequest());

        indexerClient.ProcessBatchAnalysisCalls.ShouldBe(1);
        indexerClient.LastBatchRepo.ShouldBeNull();
    }
}
