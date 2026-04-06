using MassTransit;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models.Messages;
using TC.Common.Configuration;
using TC.Common.TcServiceStack.Queue;
using TC.Jarvis.DependencyInjection;

namespace TC.CodeGraphApi.Consumers;

/// <summary>
/// Cascading cleanup when a repository is removed from GitLab.
/// Deletes nodes, edges, analysis records, cross-repo links, and vitals data.
/// </summary>
public class RepositoryRemovedConsumer : TcConsumer<RepositoryRemoved, RepositoryRemovedConsumer>
{
    private readonly IScope _scope;
    private readonly ITcConfiguration<CodeGraphServiceSettings> _settings;
    private readonly ILogger<RepositoryRemovedConsumer> _logger1;

    /// <summary>
    /// Cascading cleanup when a repository is removed from GitLab.
    /// Deletes nodes, edges, analysis records, cross-repo links, and vitals data.
    /// </summary>
    public RepositoryRemovedConsumer(IScope scope,
        ITcConfiguration<CodeGraphServiceSettings> settings,
        ILogger<RepositoryRemovedConsumer> logger) : base(logger)
    {
        _scope = scope;
        _settings = settings;
        _logger1 = logger;
        TcConsumerConfigurator.QueueSuffix = typeof(RepositoryRemoved).FullName;
    }

    public override Action<IInstanceConfigurator<RepositoryRemovedConsumer>> InstanceConfigurator
    {
        get { return configurator => ConsumerConfiguration.ConfigureRetries(configurator, _settings); }
    }
    
    public override async Task Consume(RepositoryRemoved message, ConsumeContext<RepositoryRemoved> consumeContext)
    {
        _logger1.LogInformation("Cleaning up data for removed repository {Repo}", message.Name);

        using var childScope = _scope.CreateChildScope();
        var store = childScope.GetInstance<IGraphStore>();

        // Delete in dependency order: cross-repo edges → analysis → edges → nodes → vitals → sync state → repo
        await store.DeleteCrossRepoEdgesForProjectAsync(message.Name);
        await store.DeleteAnalysisDataForProjectAsync(message.Name);
        await store.DeleteAllEdgesForProjectAsync(message.Name);
        await store.DeleteNodesByProjectAsync(message.Name);
        await store.DeleteFileMetricsAsync(message.Name);
        await store.DeleteSyncStateAsync(message.Name);
        await store.DeleteRepositoryAsync(message.Name);

        _logger1.LogInformation("Cleanup complete for removed repository {Repo}", message.Name);
    }
}
