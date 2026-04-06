using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models.Messages;
using TC.CodeGraphApi.Models.Requests;
using TC.Common.TcServiceStack.Gateway.Abstractions;
using TC.Common.TcServiceStack.Queue.Abstractions;
using TC.JobUtilities;

namespace TC.CodeGraphJobs.Jobs;

public class DiscoverRepositoriesJob(
    ILogger<DiscoverRepositoriesJob> logger,
    ITcGateway tcGateway,
    ITcServiceBus serviceBus,
    Guid instanceKey)
    : Job(logger, serviceBus, instanceKey)
{
    protected override async Task ExecuteAsync(StartJob startJob)
    {
        var response = await tcGateway.SendAsync(new DiscoverRequest() {
            IncludeAllSource = true,
            Limit = null,
            ShouldIndex = true,
            ShouldAnalyze = true,
            SkipIfUpToDate = true
        });

        if (response.Success)
        {
            logger.LogInformation("Discovered {newProjects} new projects. Skipped {skippedProjects} existing projects. Analyzing {analyzedProjects} projects from all groups.",
                response.Result.NewCount, response.Result.Skipped, response.Result.Published);
            return;
        }
        throw new Exception("Discover request failed", response.Exception);
    }
}
