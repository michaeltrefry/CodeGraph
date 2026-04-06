# Gateway Pattern (Inter-Service HTTP)

Services communicate over HTTP using `ITcGateway`, not direct `HttpClient` calls. The gateway handles service discovery, serialization, error wrapping, and retry logic.

## ITcGateway Interface

```csharp
public interface ITcGateway
{
    // Standard calls — routing determined by [TcServiceDto] attribute on the request DTO
    Task<TcResponse> SendAsync(IReturns requestDto);
    Task<TcResponse<TResponse>> SendAsync<TResponse>(IReturns<TResponse> requestDto);
    TcResponse Send(IReturns requestDto);
    TcResponse<TResponse> Send<TResponse>(IReturns<TResponse> requestDto);

    // Explicit routing — service name + path passed directly
    Task<TcResponse> SendToServiceAsync(HttpMethod method, string serviceName, string path, IReturns requestDto);
    Task<TcResponse<TResponse>> SendToServiceAsync<TResponse>(HttpMethod method, string serviceName, string path, IReturns<TResponse> requestDto);
    // ... sync overloads with same signature ...
}
```

## Standard Pattern (~90% of calls)

The request DTO carries the routing information via `[TcServiceDto]`:

```csharp
// In TC.DomainBlacklistApi.Models (NuGet package)
[TcServiceDto("DomainBlacklistApi", "CheckBlacklist", "GET")]
public class CheckBlacklist : IReturns<DomainBlacklistResult>, IReturns
{
    public string ProperCasedDomain { get; set; }
    public int NameBrightAccountId { get; set; }
}
```

### TcServiceDto Attribute

```
[TcServiceDto(serviceName, routeName, httpMethod)]
```

- **serviceName**: The API project name without "TC." prefix. `"DomainBlacklistApi"` routes to `TC.DomainBlacklistApi`.
- **routeName**: The route/action name within that service.
- **httpMethod**: HTTP verb — `"GET"`, `"POST"`, `"PUT"`, `"DELETE"`.

### Calling Code

```csharp
// In some other service
var result = await _tcGateway.SendAsync<DomainBlacklistResult>(new CheckBlacklist
{
    ProperCasedDomain = domain,
    NameBrightAccountId = accountId
});
```

The gateway reads the `[TcServiceDto]` attribute at runtime to determine where to route the request.

## Explicit Routing Pattern (~10% of calls)

Used when routing can't be determined from a DTO attribute:

```csharp
var result = await _tcGateway.SendToServiceAsync(
    HttpMethod.Get,
    "OrdersApi",          // service name (without TC. prefix)
    "/api/orders/search", // explicit path
    requestDto);
```

## Response Wrapper

All gateway calls return `TcResponse` or `TcResponse<T>`, which wraps the result with success/failure status and error details.

## Identifying Cross-Service Dependencies

To find what services a repo calls:
1. Search for `ITcGateway` injection / usage
2. Look at the request DTO types passed to `Send`/`SendAsync`
3. The `[TcServiceDto]` attribute on each DTO reveals the target service
4. The DTO's NuGet package origin (`TC.*.Models`) confirms the dependency
