using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Jobs.Tests.Jobs;

public class DiscoverRepositoriesJobTests
{
    [Fact]
    public async Task SendsDiscoverRequestWithSuppliedValues()
    {
        var indexerClient = new RecordingIndexerClient
        {
            NextAcceptedResponse = new IndexerAcceptedResponse("queued", "Queued repository discovery.", 123, "/api/indexer/runs/123")
        };
        var job = new DiscoverRepositoriesJob(indexerClient, NullLogger<DiscoverRepositoriesJob>.Instance);
        var request = new DiscoverRequest
        {
            ShouldIndex = false,
            ShouldAnalyze = true,
            SkipIfUpToDate = false,
            IncludeAllSource = true,
            NamePattern = "orders",
            Limit = 25
        };

        var result = await job.ExecuteAsync(request);

        indexerClient.LastDiscoverRequest.ShouldNotBeNull();
        indexerClient.LastDiscoverRequest.ShouldIndex.ShouldBeFalse();
        indexerClient.LastDiscoverRequest.ShouldAnalyze.ShouldBeTrue();
        indexerClient.LastDiscoverRequest.SkipIfUpToDate.ShouldBeFalse();
        indexerClient.LastDiscoverRequest.IncludeAllSource.ShouldBeTrue();
        indexerClient.LastDiscoverRequest.NamePattern.ShouldBe("orders");
        indexerClient.LastDiscoverRequest.Limit.ShouldBe(25);
        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Queued repository discovery");
    }
}
