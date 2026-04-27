using System.Security.Claims;

namespace CodeGraph.Host.Shared.Auth;

public sealed record InternalServiceIdentityValidationResult(
    bool IsValid,
    ClaimsPrincipal? Principal,
    string? Error)
{
    public static InternalServiceIdentityValidationResult Success(ClaimsPrincipal principal) =>
        new(true, principal, null);

    public static InternalServiceIdentityValidationResult Failure(string error) =>
        new(false, null, error);
}
