using Shouldly;
using CodeGraph.Models.Responses;
using CodeGraph.Jobs.Jobs;

namespace CodeGraph.Jobs.Tests.Jobs;

public class DiscoverRepositoriesJobTests
{
    [Fact]
    public async Task SendsDiscoverRequestWithExpectedDefaults()
    {
        var adminService = new RecordingAdminService
        {
            NextDiscoverResponse = new DiscoverResponse(10, 8, 6, 3, 2, ["Orders.Api"])
        };
        var job = new TestDiscoverRepositoriesJob(adminService);

        await job.InvokeAsync(new StartJob { Args = [] });

        adminService.LastDiscoverRequest.ShouldNotBeNull();
        adminService.LastDiscoverRequest.ShouldIndex.ShouldBeTrue();
        adminService.LastDiscoverRequest.ShouldAnalyze.ShouldBeTrue();
        adminService.LastDiscoverRequest.SkipIfUpToDate.ShouldBeTrue();
        adminService.LastDiscoverRequest.IncludeAllSource.ShouldBeTrue();
        adminService.LastDiscoverRequest.Limit.ShouldBeNull();
    }
}
