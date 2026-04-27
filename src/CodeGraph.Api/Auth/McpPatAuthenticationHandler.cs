using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CodeGraph.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodeGraph.Api.Auth;

public sealed class McpPatAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    McpPersonalAccessTokenService personalAccessTokenService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
            return AuthenticateResult.NoResult();

        if (!AuthenticationHeaderValue.TryParse(authorization, out var headerValue) ||
            !string.Equals(headerValue.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return AuthenticateResult.Fail("A bearer MCP personal access token is required.");
        }

        var token = headerValue.Parameter.Trim();
        if (!personalAccessTokenService.IsValidTokenFormat(token))
            return AuthenticateResult.Fail("The MCP personal access token format is invalid.");

        var validation = await personalAccessTokenService.ValidateAsync(
            token,
            ResolveLastUsedFrom(),
            Context.RequestAborted);

        if (validation is null)
            return AuthenticateResult.Fail("The MCP personal access token is invalid, expired, or revoked.");

        var claims = new List<Claim>
        {
            new("preferred_username", validation.Username),
            new("username", validation.Username),
            new(ClaimTypes.Name, validation.Username),
            new("mcp_pat_token_id", validation.TokenId.ToString()),
            new("mcp_pat_token_name", validation.TokenName)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    private string? ResolveLastUsedFrom()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
            return forwardedFor.Split(',')[0].Trim();

        var remoteIp = Context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(remoteIp))
            return remoteIp;

        var userAgent = Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }
}
