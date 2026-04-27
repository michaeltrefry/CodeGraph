using System.Security.Claims;

namespace CodeGraph.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static string? GetUsername(this ClaimsPrincipal user)
    {
        return user.FindFirstValue("preferred_username")
               ?? user.FindFirstValue("username")
               ?? user.FindFirstValue(ClaimTypes.Name);
    }
}
