using MassTransit;
using CodeGraph.Data;
using CodeGraph.Models.Messages;

namespace CodeGraph.Indexer.Host.Consumers;

/// <summary>
/// Cascading cleanup when a repository is removed from the configured source provider.
/// Deletes nodes, edges, analysis records, cross-repo links, and vitals data.
/// </summary>
public class RepositoryRemovedConsumer(
    IGraphStore store,
    ILogger<RepositoryRemovedConsumer> logger) : IConsumer<RepositoryRemoved>
{
    public async Task Consume(ConsumeContext<RepositoryRemoved> context)
    {
        var message = context.Message;
        logger.LogInformation("Cleaning up data for removed repository {Repo}", message.Name);

        await store.DeleteCrossRepoEdgesForProjectAsync(message.Name);
        await store.DeleteAnalysisDataForProjectAsync(message.Name);
        await store.DeleteAllEdgesForProjectAsync(message.Name);
        await store.DeleteNodesByProjectAsync(message.Name);
        await store.DeleteFileMetricsAsync(message.Name);
        await store.DeleteSyncStateAsync(message.Name);
        await store.DeleteRepositoryAsync(message.Name);

        logger.LogInformation("Cleanup complete for removed repository {Repo}", message.Name);
    }
}
