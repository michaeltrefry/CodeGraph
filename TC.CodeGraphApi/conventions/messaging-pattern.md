# Messaging Pattern (RabbitMQ / ServiceBus)

Asynchronous inter-service communication uses RabbitMQ via an in-house `ITcServiceBus` abstraction built on top of MassTransit.

## ITcServiceBus Interface

```csharp
public interface ITcServiceBus
{
    // Publish event to the default virtual host
    Task<TcPublishResponse> Publish<T>(T eventObject, bool allowFailedPublishLogging = true) where T : class;

    // Publish event to a specific virtual host
    Task<TcPublishResponse> PublishToVirtualHost<T>(T eventObject, TcQueueHosts queueHost, bool allowFailedPublishLogging = true) where T : class;

    // Send command to a specific named queue
    Task SendCommandToCustomQueue<T>(T messageToSend, TcQueueHosts queueHost, string queueName, bool durable = true) where T : class;
}
```

## Publishing Events

Event types are POCOs defined in the publishing service's Models project:

```csharp
// In TC.OrdersApi.Models (NuGet package)
public class OrderCreatedEvent
{
    public int OrderId { get; set; }
    public string CustomerEmail { get; set; }
    public decimal Total { get; set; }
}
```

Publishing:

```csharp
// In TC.OrdersApi.Services
await _serviceBus.Publish(new OrderCreatedEvent
{
    OrderId = order.Id,
    CustomerEmail = order.Email,
    Total = order.Total
});
```

## Consuming Events

Consumers inherit from `Consumer<T>` (in-house base class) or implement `IConsumer<T>` (MassTransit):

```csharp
// In TC.NotificationsApi.Services
public class OrderCreatedConsumer : Consumer<OrderCreatedEvent>
{
    public override async Task Consume(ConsumeContext<OrderCreatedEvent> context)
    {
        var message = context.Message;
        await SendOrderConfirmationEmail(message.CustomerEmail, message.OrderId);
    }
}
```

Consumers are registered in `Startup.cs` via DI.

## Queue Routing

- Queue names are derived from the event type name by convention
- `TcQueueHosts` enum specifies which RabbitMQ virtual host to use
- Queue-routing attributes on event types can override defaults

## Identifying Cross-Service Messaging Dependencies

To find what events a service publishes or consumes:
1. **Publishers**: Search for `_serviceBus.Publish`, `PublishToVirtualHost`, or `SendCommandToCustomQueue` — the generic type argument is the event type
2. **Consumers**: Search for classes that inherit `Consumer<T>` or implement `IConsumer<T>` — the type argument is the consumed event
3. The event type's namespace reveals the source service (e.g., `TC.OrdersApi.Models.OrderCreatedEvent` → published by TC.OrdersApi)
