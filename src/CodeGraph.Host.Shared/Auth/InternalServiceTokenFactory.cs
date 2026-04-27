using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CodeGraph.Host.Shared.Auth;

public sealed class InternalServiceTokenFactory(IOptions<InternalServiceAuthOptions> optionsAccessor) : IInternalServiceTokenFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string CreateToken(string username, string audience)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled)
            return string.Empty;

        options.Validate();

        var normalizedUsername = NormalizeRequired(username, nameof(username)).ToLowerInvariant();
        var normalizedAudience = NormalizeRequired(audience, nameof(audience));
        var now = DateTimeOffset.UtcNow;
        var envelope = new InternalServiceIdentityEnvelope(
            normalizedUsername,
            normalizedAudience,
            options.Issuer.Trim(),
            now.ToUnixTimeSeconds(),
            now.AddSeconds(options.TokenLifetimeSeconds).ToUnixTimeSeconds(),
            Guid.NewGuid().ToString("N"));

        var payload = InternalServiceTokenCodec.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
        var signature = InternalServiceTokenCodec.Base64UrlEncode(InternalServiceTokenCodec.Sign(payload, options.HmacKey));
        return $"{payload}.{signature}";
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }
}
