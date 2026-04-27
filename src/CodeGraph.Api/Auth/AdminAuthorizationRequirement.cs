using System.Security.Claims;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace CodeGraph.Api.Auth;

public sealed class AdminAuthorizationRequirement : IAuthorizationRequirement;

public sealed class AdminAuthorizationHandler(
    IServiceProvider serviceProvider,
    IOptions<AuthOptions> authOptions,
    ILogger<AdminAuthorizationHandler> logger)
    : AuthorizationHandler<AdminAuthorizationRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminAuthorizationRequirement requirement)
    {
        if (IsAdminClaimPresent(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var username = context.User.GetUsername();
        if (string.IsNullOrWhiteSpace(username))
            return;

        var normalizedUsername = username.Trim().ToLowerInvariant();
        if (!authOptions.Value.Enabled && authOptions.Value.LocalDevIsAdmin)
        {
            context.Succeed(requirement);
            return;
        }

        using var scope = serviceProvider.CreateScope();
        var adminStore = scope.ServiceProvider.GetService<IAdminStore>();
        if (adminStore is null)
        {
            logger.LogDebug("Admin authorization skipped database lookup because IAdminStore is not registered.");
            return;
        }

        if (await adminStore.IsAdminAsync(normalizedUsername))
            context.Succeed(requirement);
    }

    private static bool IsAdminClaimPresent(ClaimsPrincipal user)
    {
        return user.IsInRole("admin")
               || user.HasClaim("role", "admin")
               || user.HasClaim("roles", "admin")
               || user.HasClaim("codegraph_admin", "true");
    }
}
