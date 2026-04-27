namespace CodeGraph.Host.Shared.Auth;

public interface IInternalServiceTokenValidator
{
    InternalServiceIdentityValidationResult ValidateToken(string? token, string expectedAudience);
}
