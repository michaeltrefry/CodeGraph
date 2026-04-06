using MassTransit;
using TC.CodeGraphApi.Models.Messages;
using TC.CodeGraphApi.Services;
using TC.Common.Configuration;
using TC.Common.TcServiceStack.Queue;
using TC.Jarvis.DependencyInjection;

namespace TC.CodeGraphApi.Consumers;

public class ProcessRepositoryConsumer : TcConsumer<ProcessRepository, ProcessRepositoryConsumer>
{
    private readonly IScope _scope;
    private readonly ITcConfiguration<CodeGraphServiceSettings> _settings;

    public ProcessRepositoryConsumer(IScope scope, 
        ITcConfiguration<CodeGraphServiceSettings> settings, 
        ILogger<ProcessRepositoryConsumer> logger) : base(logger)
    {
        _scope = scope;
        _settings = settings;
        TcConsumerConfigurator.QueueSuffix = typeof(ProcessRepository).FullName;
    }

    public override Action<IInstanceConfigurator<ProcessRepositoryConsumer>> InstanceConfigurator
    {
        get { return configurator => ConsumerConfiguration.ConfigureRetries(configurator, _settings); }
    }
    
    public override async Task Consume(ProcessRepository message, ConsumeContext<ProcessRepository> consumeContext)
    {
        using var childScope = _scope.CreateChildScope();
        var service = childScope.GetInstance<IProjectService>();
        await service.ProcessRepository(message, consumeContext.CancellationToken);
    }
}
