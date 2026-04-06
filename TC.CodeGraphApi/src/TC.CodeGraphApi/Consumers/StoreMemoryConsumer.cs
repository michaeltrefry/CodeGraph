using MassTransit;
using TC.CodeGraphApi.Models.Messages;
using TC.CodeGraphApi.Services.Memory;
using TC.Common.Configuration;
using TC.Common.TcServiceStack.Queue;
using TC.Jarvis.DependencyInjection;

namespace TC.CodeGraphApi.Consumers;

public class StoreMemoryConsumer : TcConsumer<StoreMemory, StoreMemoryConsumer>
{
    private readonly IScope _scope;
    private readonly ITcConfiguration<CodeGraphServiceSettings> _settings;

    public StoreMemoryConsumer(IScope scope,
        ITcConfiguration<CodeGraphServiceSettings> settings,
        ILogger<StoreMemoryConsumer> logger) : base(logger)
    {
        _scope = scope;
        _settings = settings;
        TcConsumerConfigurator.QueueSuffix = typeof(StoreMemory).FullName;
    }

    public override Action<IInstanceConfigurator<StoreMemoryConsumer>> InstanceConfigurator
    {
        get { return configurator => ConsumerConfiguration.ConfigureRetries(configurator, _settings); }
    }

    public override async Task Consume(StoreMemory message, ConsumeContext<StoreMemory> consumeContext)
    {
        using var childScope = _scope.CreateChildScope();
        var memoryService = childScope.GetInstance<MemoryService>();
        await memoryService.StoreStructuredAsync(message.Username, message.Extraction, message.Source);
    }
}
