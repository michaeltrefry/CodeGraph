# NuGet Package Conventions

Internal NuGet packages are the primary mechanism for sharing contracts across services.

## Package Naming

Every `TC.*.Models` project is published as a NuGet package with the same name:

- `TC.OrdersApi.Models` → NuGet package `TC.OrdersApi.Models`
- `TC.Common.ServiceStack` → NuGet package `TC.Common.ServiceStack`

## Package Contents

Models packages contain **public contracts only**:
- Request/response DTOs (with `[TcServiceDto]` attributes)
- Event types (for `ITcServiceBus` publish/consume)
- Shared enums and constants
- Interfaces like `IReturns<T>`

They do **not** contain business logic, data access, or service implementations.

## Cross-Repo Linking

The qualified type name from a Models package is the canonical linking key:

```
TC.OrdersApi.Models.OrderCreatedEvent
      ↑                    ↑
  Source service      Event type name
```

When Service B references `TC.OrdersApi.Models` and uses `OrderCreatedEvent`, that establishes a dependency from Service B → TC.OrdersApi.

## Deriving the Source Service

Strip known suffixes from the package name to find the owning project:

| Package Name | Owning Project |
|---|---|
| `TC.OrdersApi.Models` | `TC.OrdersApi` |
| `TC.OrdersApi.Contracts` | `TC.OrdersApi` |
| `TC.OrdersApi.Client` | `TC.OrdersApi` |
| `TC.Common.ServiceStack` | `TC.Common.ServiceStack` (itself) |

Known suffixes: `.Models`, `.Contracts`, `.Client`, `.Shared`

## Foundational Packages

`TC.Common.ServiceStack` and ~12 other framework repos define shared abstractions used by all services:
- `ITcGateway` — inter-service HTTP
- `ITcServiceBus` — messaging
- `Consumer<T>` — message consumer base class
- `TcServiceDto` attribute — gateway routing
- Base classes, middleware, common utilities

These must be understood first to interpret patterns in all other repos.
