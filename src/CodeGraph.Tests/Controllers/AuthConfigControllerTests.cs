using System.Security.Claims;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Controllers;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class AuthConfigControllerTests
{
    [Fact]
    public void GetAuthConfig_ReturnsStandaloneClientConfiguration()
    {
        var controller = CreateController(
            new AuthOptions
            {
                Enabled = true,
                Authority = "https://identity.trefry.net/realms/trefry",
                AuthorizationUrl = "https://identity.trefry.net/realms/trefry/protocol/openid-connect/auth",
                TokenUrl = "https://identity.trefry.net/realms/trefry/protocol/openid-connect/token",
                EndSessionUrl = "https://identity.trefry.net/realms/trefry/protocol/openid-connect/logout",
                ClientId = "codegraph-web",
                Audience = "codegraph-api",
                Scope = "openid profile email"
            },
            [],
            isAdmin: false);

        var result = controller.GetAuthConfig();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<AuthConfigResponse>();
        response.Enabled.ShouldBeTrue();
        response.Authority.ShouldBe("https://identity.trefry.net/realms/trefry");
        response.AuthorizationUrl.ShouldBe("https://identity.trefry.net/realms/trefry/protocol/openid-connect/auth");
        response.TokenUrl.ShouldBe("https://identity.trefry.net/realms/trefry/protocol/openid-connect/token");
        response.EndSessionUrl.ShouldBe("https://identity.trefry.net/realms/trefry/protocol/openid-connect/logout");
        response.ClientId.ShouldBe("codegraph-web");
        response.Audience.ShouldBe("codegraph-api");
        response.Scope.ShouldBe("openid profile email");
    }

    [Fact]
    public async Task GetCurrentUser_UsesSharedUsernameFallbackChain()
    {
        var controller = CreateController(
            new AuthOptions(),
            [new Claim("preferred_username", "Michael")],
            isAdmin: true);

        var result = await controller.GetCurrentUser();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<CurrentUserResponse>();
        response.Username.ShouldBe("michael");
        response.IsAdmin.ShouldBeTrue();
    }

    [Fact]
    public async Task GetCurrentUser_ReturnsUnauthorized_WhenNoSupportedUsernameClaimExists()
    {
        var controller = CreateController(
            new AuthOptions(),
            [new Claim(ClaimTypes.Email, "michael@example.com")],
            isAdmin: true);

        var result = await controller.GetCurrentUser();

        var unauthorized = result.Result.ShouldBeOfType<UnauthorizedObjectResult>();
        GetValue<string>(unauthorized.Value!, "error").ShouldBe("Username claim is required");
    }

    private static AuthConfigController CreateController(
        AuthOptions options,
        IEnumerable<Claim> claims,
        bool isAdmin)
    {
        return new AuthConfigController(
            Options.Create(options),
            new FakeAuthorizationService(isAdmin))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    private static T GetValue<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        property.ShouldNotBeNull();
        return (T)property.GetValue(value)!;
    }

    private sealed class FakeAuthorizationService(bool isAdmin) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(isAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            policyName.ShouldBe(CodeGraphAuthenticationDefaults.AdminPolicy);
            return Task.FromResult(isAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }
}
