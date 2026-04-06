---
name: create-consumer
description: Scaffold a new RabbitMQ/MassTransit consumer following team conventions
allowed-tools: WebFetch, Read, Write, Edit, Grep, Glob, Bash
---

# Create a New Consumer

You are creating a new MassTransit/ServiceBus consumer for the CodeGraph project.

## Step 1: Fetch the messaging convention

Fetch the team's authoritative messaging pattern from the conventions API:

```
GET http://localhost:5037/api/conventions/messaging-pattern-rabbitmq-servicebus
```

Use WebFetch to retrieve this. The response JSON has a `content` field with the full convention in markdown. **This is your primary reference for how consumers must be structured.** Follow it precisely.

If the convention API is unavailable, fall back to reading existing consumers in `src/TC.CodeGraphApi/Consumers/` as reference patterns.

## Step 2: Gather requirements from the user

If not already provided via $ARGUMENTS, ask the user for:
1. **Consumer name** — what event/message does it consume? (e.g., "RepositoryAnalysisCompleted")
2. **What it does** — brief description of the consumer's responsibility
3. **Which bus** — Enterprise or Domains (default: Enterprise)
4. **Retry strategy** — default retries, delayed redelivery, or custom (default: standard retries)

## Step 3: Create the message class (if needed)

Check if the message type already exists in `src/TC.CodeGraphApi.Models/Messages/`.

If not, create it following this pattern:
- File: `src/TC.CodeGraphApi.Models/Messages/{MessageName}.cs`
- Namespace: `TC.CodeGraphApi.Models.Messages`
- Decorate with `[TcServiceBusEvent(TcQueueHosts.Enterprise)]` (or Domains)
- Simple POCO with auto-properties

## Step 4: Create the consumer class

Create the consumer in `src/TC.CodeGraphApi/Consumers/{ConsumerName}Consumer.cs`.

Key structural requirements:
- Inherit from `TcConsumer<TMessage, TConsumer>`
- Constructor takes `IScope`, `ITcConfiguration<CodeGraphServiceSettings>`, `ILogger<T>` and calls `base(logger)`
- Set `TcConsumerConfigurator.QueueSuffix = typeof(TMessage).FullName` in the constructor
- Override `InstanceConfigurator` for retry configuration
- Override `Consume(TMessage message, ConsumeContext<TMessage> consumeContext)` with the business logic
- Use `_scope.CreateChildScope()` with `using` to resolve scoped dependencies
- Use `consumeContext.CancellationToken` and pass it through async calls

## Step 5: Register in Startup.cs

Add the consumer registration in `src/TC.CodeGraphApi/Startup.cs` inside the `AddTcQueueing` block:
- Enterprise bus: chain onto `builder.RegisterEnterpriseBus()`
- Domains bus: chain onto `builder.RegisterDomainsBus()`

Use: `.AddConsumer<{Name}Consumer, DefaultConsumerDefinition<{Name}Consumer>>()`

Add the required `using` statement for the consumer namespace if not already present.

## Step 6: Summary

After creating all files, provide a summary of:
- Files created/modified
- The message type and queue it listens on
- Any services the user still needs to implement or wire up
