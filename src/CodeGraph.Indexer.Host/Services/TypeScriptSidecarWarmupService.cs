using CodeGraph.Extractors.TypeScript;

namespace CodeGraph.Indexer.Host.Services;

public sealed class TypeScriptSidecarWarmupService(
    TypeScriptServerManager sidecar,
    ILogger<TypeScriptSidecarWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (bool.TryParse(Environment.GetEnvironmentVariable("CODEGRAPH_SKIP_TS_SIDECAR_WARMUP"), out var skipWarmup) &&
            skipWarmup)
        {
            logger.LogInformation("Skipping TypeScript sidecar warmup because CODEGRAPH_SKIP_TS_SIDECAR_WARMUP is enabled.");
            return;
        }

        if (await sidecar.EnsureStartedAsync(cancellationToken))
        {
            logger.LogInformation("TypeScript sidecar warmup succeeded.");
            return;
        }

        logger.LogWarning("TypeScript sidecar warmup failed.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
