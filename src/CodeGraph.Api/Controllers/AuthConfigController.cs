using CodeGraph.Api.Auth;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthConfigController(
    IOptions<AuthOptions> authOptions,
    IAuthorizationService authorizationService) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("config")]
    public ActionResult<AuthConfigResponse> GetAuthConfig()
    {
        var auth = authOptions.Value;
        return Ok(new AuthConfigResponse(
            auth.Enabled,
            auth.Authority,
            auth.AuthorizationUrl,
            auth.TokenUrl,
            auth.EndSessionUrl,
            auth.ClientId,
            auth.Audience,
            auth.Scope));
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.UserPolicy)]
    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> GetCurrentUser()
    {
        var username = User.GetUsername()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(username))
            return Unauthorized(new { error = "Username claim is required" });

        var adminAuthorization = await authorizationService.AuthorizeAsync(
            User,
            resource: null,
            CodeGraphAuthenticationDefaults.AdminPolicy);

        return Ok(new CurrentUserResponse(username, adminAuthorization.Succeeded));
    }
}
