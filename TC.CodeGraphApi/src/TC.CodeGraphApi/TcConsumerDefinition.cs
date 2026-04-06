using MassTransit;

namespace TC.CodeGraphApi;

public class TcConsumerDefinition<TConsumer> : 
    ConsumerDefinition<TConsumer>
    where TConsumer : class, IConsumer
{
    public TcConsumerDefinition()
    {
        EndpointName = typeof(TConsumer).FullName;
    }
}