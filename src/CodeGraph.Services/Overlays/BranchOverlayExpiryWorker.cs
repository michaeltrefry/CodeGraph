using CodeGraph.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Overlays;

/// <summary>
/// Periodically deletes branch overlays older than the configured TTL so overlays stay bounded
/// without human attention.
/// </summary>
public sealed class BranchOverlayExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<BranchOverlayOptions> optionsAccessor,
    ILogger<BranchOverlayExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(optionsAccessor.Value.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBranchOverlayService>().ExpireOverlaysAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Branch overlay TTL sweep failed");
        }
    }
}
