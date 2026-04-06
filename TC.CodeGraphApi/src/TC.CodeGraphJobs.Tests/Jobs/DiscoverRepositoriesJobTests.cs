using Shouldly;
using TC.CodeGraphApi.Models.Responses;
using TC.JobUtilities;

namespace TC.CodeGraphJobs.Tests.Jobs;

public class DiscoverRepositoriesJobTests
{
    [Fact]
    public async Task SendsDiscoverRequestWithExpectedDefaults()
    {
        var gateway = new RecordingTcGateway
        {
            NextDiscoverResponse = new FakeGatewayResponse<DiscoverResponse>
            {
                Success = true,
                Result = new DiscoverResponse(10, 8, 6, 3, 2, ["TC.OrdersApi"])
            }
        };
        var serviceBus = new RecordingServiceBus();
        var job = new TestDiscoverRepositoriesJob(gateway, serviceBus);

        await job.InvokeAsync(new StartJob { Args = [] });

        gateway.LastDiscoverRequest.ShouldNotBeNull();
        gateway.LastDiscoverRequest.ShouldIndex.ShouldBeTrue();
        gateway.LastDiscoverRequest.ShouldAnalyze.ShouldBeTrue();
        gateway.LastDiscoverRequest.SkipIfUpToDate.ShouldBeTrue();
        gateway.LastDiscoverRequest.IncludeAllSource.ShouldBeTrue();
        gateway.LastDiscoverRequest.Limit.ShouldBeNull();
    }

    [Fact]
    public async Task FailedGatewayResponse_ThrowsWithInnerException()
    {
        var inner = new InvalidOperationException("Gateway failed");
        var gateway = new RecordingTcGateway
        {
            NextDiscoverResponse = new FakeGatewayResponse<DiscoverResponse>
            {
                Success = false,
                Exception = inner
            }
        };
        var serviceBus = new RecordingServiceBus();
        var job = new TestDiscoverRepositoriesJob(gateway, serviceBus);

        var ex = await Should.ThrowAsync<Exception>(() => job.InvokeAsync(new StartJob { Args = [] }));

        ex.Message.ShouldBe("Discover request failed");
        ex.InnerException.ShouldBe(inner);
    }
}
