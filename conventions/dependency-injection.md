# Dependency Injection Conventions

All services use **Autofac** as the DI container, registered in `Startup.cs`.

## Registration Pattern

```csharp
// In Startup.cs
public void ConfigureContainer(ContainerBuilder builder)
{
    builder.RegisterType<OrderService>().As<IOrderService>().InstancePerLifetimeScope();
    builder.RegisterType<OrderRepository>().As<IOrderRepository>().InstancePerLifetimeScope();
    builder.RegisterType<OrderCreatedConsumer>().AsSelf().InstancePerLifetimeScope();
}
```

## Common Lifetimes

- `InstancePerLifetimeScope()` — one instance per HTTP request (most common)
- `SingleInstance()` — singleton
- `InstancePerDependency()` — transient (new instance every injection)

## Injected Infrastructure

Standard services inject these framework-provided interfaces:

| Interface | Purpose |
|---|---|
| `ITcGateway` | Inter-service HTTP calls |
| `ITcServiceBus` | RabbitMQ messaging |
| `ILogger<T>` | Structured logging |
| `IConfiguration` | App settings |
| `IMemoryCache` / `IDistributedCache` | Caching |

## Constructor Injection

All dependencies are injected via constructor:

```csharp
public class OrderService : IOrderService
{
    private readonly IOrderRepository _repo;
    private readonly ITcGateway _gateway;
    private readonly ITcServiceBus _serviceBus;

    public OrderService(IOrderRepository repo, ITcGateway gateway, ITcServiceBus serviceBus)
    {
        _repo = repo;
        _gateway = gateway;
        _serviceBus = serviceBus;
    }
}
```

## Identifying Dependencies

To understand what a service depends on:
1. Look at `Startup.cs` / `ConfigureContainer` for all registrations
2. Look at constructor parameters of service classes
3. `ITcGateway` injection → this service makes HTTP calls to other services
4. `ITcServiceBus` injection → this service publishes messages
5. `Consumer<T>` registrations → this service consumes messages
