using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using CodeGraph.Services;

namespace CodeGraph.Api.Auth;

public class AdminAuthorizationHandler(IServiceScopeFactory scopeFactory) : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminRequirement requirement)
    {
        var username = context.User.FindFirstValue("preferred_username")
                       ?? context.User.FindFirstValue("username")
                       ?? context.User.FindFirstValue(ClaimTypes.Name);

        if (string.IsNullOrEmpty(username))
            return;

        // Use a scope since this handler is registered as singleton
        using var scope = scopeFactory.CreateScope();
        var adminUserService = scope.ServiceProvider.GetRequiredService<IAdminUserService>();

        if (await adminUserService.IsAdminAsync(username))
            context.Succeed(requirement);
    }
}
