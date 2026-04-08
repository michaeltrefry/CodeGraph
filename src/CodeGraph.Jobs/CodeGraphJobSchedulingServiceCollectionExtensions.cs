using Microsoft.Extensions.DependencyInjection;
using CodeGraph.Jobs.Jobs;

namespace CodeGraph.Jobs;

public static class CodeGraphJobSchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddCodeGraphJobScheduling(this IServiceCollection services)
    {
        services.AddTransient<DiscoverRepositoriesJob>();
        services.AddTransient<ReIndexAllRepositoriesJob>();
        services.AddTransient<ProcessBatchAnalysisJob>();
        services.AddTransient<LinkAndDetectJob>();
        services.AddTransient<DetectCommunitiesJob>();
        services.AddTransient<RegenerateMcpDocsJob>();
        services.AddTransient<IJobCommandDispatcher, JobCommandDispatcher>();
        services.AddTransient<IJobScheduleService, JobScheduleService>();
        return services;
    }
}
