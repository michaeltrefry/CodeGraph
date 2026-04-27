namespace CodeGraph.Host.Shared.Auth;

public sealed record InternalServiceIdentityEnvelope(
    string Username,
    string Audience,
    string Issuer,
    long IssuedAtUnixTimeSeconds,
    long ExpiresAtUnixTimeSeconds,
    string Nonce);
