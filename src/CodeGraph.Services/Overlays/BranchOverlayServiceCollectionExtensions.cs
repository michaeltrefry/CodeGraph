using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Services.Overlays;

public static class BranchOverlayServiceCollectionExtensions
{
    /// <summary>
    /// Registers branch overlay indexing and its TTL sweep. Requires the MariaDB storage provider
    /// (<see cref="CodeGraph.Data.IBranchOverlayStore"/>), an <see cref="Pipeline.IndexingPipeline"/>,
    /// and an <see cref="IRepoProvider"/>.
    /// </summary>
    public static IServiceCollection AddBranchOverlays(this IServiceCollection services)
    {
        services.AddSingleton<BranchOverlayGit>();
        services.AddTransient<IBranchOverlayService, BranchOverlayService>();
        services.AddHostedService<BranchOverlayExpiryWorker>();
        return services;
    }
}
