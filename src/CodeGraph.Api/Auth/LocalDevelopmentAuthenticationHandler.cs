using System.Security.Claims;
using System.Text.Encodings.Web;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodeGraph.Api.Auth;

public sealed class LocalDevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthOptions> authOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = authOptions.Value;
        if (configured.Enabled)
            return Task.FromResult(AuthenticateResult.NoResult());

        var username = string.IsNullOrWhiteSpace(configured.LocalDevUsername)
            ? "local-admin"
            : configured.LocalDevUsername.Trim().ToLowerInvariant();

        var claims = new List<Claim>
        {
            new("preferred_username", username),
            new("username", username),
            new(ClaimTypes.Name, username)
        };

        if (configured.LocalDevIsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
            claims.Add(new Claim("codegraph_admin", "true"));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
