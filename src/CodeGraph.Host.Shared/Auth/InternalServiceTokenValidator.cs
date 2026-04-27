using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CodeGraph.Host.Shared.Auth;

public sealed class InternalServiceTokenValidator(IOptions<InternalServiceAuthOptions> optionsAccessor) : IInternalServiceTokenValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public InternalServiceIdentityValidationResult ValidateToken(string? token, string expectedAudience)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled)
            return InternalServiceIdentityValidationResult.Failure("Internal service authentication is disabled.");

        try
        {
            options.Validate();
        }
        catch (Exception ex)
        {
            return InternalServiceIdentityValidationResult.Failure(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(token))
            return InternalServiceIdentityValidationResult.Failure("Internal service token is missing.");

        var parts = token.Split('.', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return InternalServiceIdentityValidationResult.Failure("Internal service token format is invalid.");

        var expectedSignature = InternalServiceTokenCodec.Sign(parts[0], options.HmacKey);
        byte[] actualSignature;
        try
        {
            actualSignature = InternalServiceTokenCodec.Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return InternalServiceIdentityValidationResult.Failure("Internal service token signature is invalid.");
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
            return InternalServiceIdentityValidationResult.Failure("Internal service token signature mismatch.");

        InternalServiceIdentityEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<InternalServiceIdentityEnvelope>(
                InternalServiceTokenCodec.Base64UrlDecode(parts[0]),
                JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return InternalServiceIdentityValidationResult.Failure("Internal service token payload is invalid.");
        }

        if (envelope is null)
            return InternalServiceIdentityValidationResult.Failure("Internal service token payload is empty.");

        var normalizedAudience = NormalizeExpected(expectedAudience, nameof(expectedAudience));
        if (!string.Equals(envelope.Audience, normalizedAudience, StringComparison.Ordinal))
            return InternalServiceIdentityValidationResult.Failure("Internal service token audience mismatch.");

        if (!string.Equals(envelope.Issuer, options.Issuer.Trim(), StringComparison.Ordinal))
            return InternalServiceIdentityValidationResult.Failure("Internal service token issuer mismatch.");

        if (string.IsNullOrWhiteSpace(envelope.Username))
            return InternalServiceIdentityValidationResult.Failure("Internal service token username is missing.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (envelope.IssuedAtUnixTimeSeconds - options.ClockSkewSeconds > now)
            return InternalServiceIdentityValidationResult.Failure("Internal service token is not valid yet.");

        if (envelope.ExpiresAtUnixTimeSeconds + options.ClockSkewSeconds < now)
            return InternalServiceIdentityValidationResult.Failure("Internal service token has expired.");

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, envelope.Username),
            new Claim("preferred_username", envelope.Username),
            new Claim("aud", envelope.Audience),
            new Claim("iss", envelope.Issuer),
            new Claim("codegraph_internal", "true")
        };
        var identity = new ClaimsIdentity(claims, "CodeGraphInternalService");
        return InternalServiceIdentityValidationResult.Success(new ClaimsPrincipal(identity));
    }

    private static string NormalizeExpected(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }
}
