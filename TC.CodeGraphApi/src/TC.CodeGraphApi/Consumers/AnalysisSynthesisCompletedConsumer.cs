using MassTransit;
using TC.CodeGraphApi.Models.Messages;
using TC.Common.Configuration;
using TC.Common.TcServiceStack.Queue;
using TC.Jarvis.DependencyInjection;

namespace TC.CodeGraphApi.Consumers;

/// <summary>
/// Writes CODEGRAPH.md files to the repo after analysis synthesis completes.
/// Isolated from synthesis so Git I/O failures can retry independently.
/// </summary>
public class AnalysisSynthesisCompletedConsumer : TcConsumer<AnalysisSynthesisCompleted, AnalysisSynthesisCompletedConsumer>
{
    private readonly IScope _scope;
    private readonly ITcConfiguration<CodeGraphServiceSettings> _settings;
    private readonly ILogger<AnalysisSynthesisCompletedConsumer> _logger1;

    /// <summary>
    /// Writes CODEGRAPH.md files to the repo after analysis synthesis completes.
    /// Isolated from synthesis so Git I/O failures can retry independently.
    /// </summary>
    public AnalysisSynthesisCompletedConsumer(IScope scope, 
        ITcConfiguration<CodeGraphServiceSettings> settings, 
        ILogger<AnalysisSynthesisCompletedConsumer> logger) : base(logger)
    {
        _scope = scope;
        _settings = settings;
        _logger1 = logger;
        TcConsumerConfigurator.QueueSuffix = typeof(AnalysisSynthesisCompleted).FullName;
    }

    public override Action<IInstanceConfigurator<AnalysisSynthesisCompletedConsumer>> InstanceConfigurator
    {
        get { return configurator => ConsumerConfiguration.ConfigureRetries(configurator, _settings); }
    }
    
    public override async Task Consume(AnalysisSynthesisCompleted message, ConsumeContext<AnalysisSynthesisCompleted> consumeContext)
    {
        var ct = consumeContext.CancellationToken;

        using var childScope = _scope.CreateChildScope();
        var batchService = childScope.GetInstance<IBatchAnalysisService>();

        _logger1.LogInformation("Writing CODEGRAPH.md files for {Repo}", message.RepoName);
        await batchService.WriteCodeGraphDocsAsync(message.RepoName, ct);
    }
}
