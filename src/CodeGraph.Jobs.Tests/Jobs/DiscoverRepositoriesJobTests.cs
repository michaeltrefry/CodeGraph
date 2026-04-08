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
        var adminService = new RecordingAdminService
        {
            NextDiscoverResponse = new DiscoverResponse(10, 8, 6, 3, 2, ["Orders.Api"])
        };
        var job = new DiscoverRepositoriesJob(adminService, NullLogger<DiscoverRepositoriesJob>.Instance);
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

        adminService.LastDiscoverRequest.ShouldNotBeNull();
        adminService.LastDiscoverRequest.ShouldIndex.ShouldBeFalse();
        adminService.LastDiscoverRequest.ShouldAnalyze.ShouldBeTrue();
        adminService.LastDiscoverRequest.SkipIfUpToDate.ShouldBeFalse();
        adminService.LastDiscoverRequest.IncludeAllSource.ShouldBeTrue();
        adminService.LastDiscoverRequest.NamePattern.ShouldBe("orders");
        adminService.LastDiscoverRequest.Limit.ShouldBe(25);
        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("published 6");
    }
}
