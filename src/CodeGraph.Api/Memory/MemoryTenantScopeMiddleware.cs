using CodeGraph.Api.Auth;
using CodeGraph.Data;

namespace CodeGraph.Api.Memory;

public sealed class MemoryTenantScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IMemoryTenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var username = MemoryTenantContext.ForAuthenticatedUser(context.User.GetUsername());
        using var tenantScope = tenantContext.Enter(username);
        await next(context);
    }
}
