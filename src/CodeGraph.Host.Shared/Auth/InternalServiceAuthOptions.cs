namespace CodeGraph.Host.Shared.Auth;

public sealed class InternalServiceAuthOptions
{
    public const string SectionPath = "CodeGraph:InternalServiceAuth";

    public bool Enabled { get; set; } = true;
    public string HmacKey { get; set; } = "";
    public string Issuer { get; set; } = "codegraph";
    public string HeaderName { get; set; } = CodeGraphInternalServiceAuthenticationDefaults.HeaderName;
    public int TokenLifetimeSeconds { get; set; } = 60;
    public int ClockSkewSeconds { get; set; } = 30;

    public void Validate()
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(HmacKey))
            throw new InvalidOperationException($"{SectionPath}:{nameof(HmacKey)} is required when internal service auth is enabled.");

        if (string.IsNullOrWhiteSpace(Issuer))
            throw new InvalidOperationException($"{SectionPath}:{nameof(Issuer)} is required when internal service auth is enabled.");

        if (string.IsNullOrWhiteSpace(HeaderName))
            throw new InvalidOperationException($"{SectionPath}:{nameof(HeaderName)} is required when internal service auth is enabled.");

        if (TokenLifetimeSeconds <= 0)
            throw new InvalidOperationException($"{SectionPath}:{nameof(TokenLifetimeSeconds)} must be greater than zero.");

        if (ClockSkewSeconds < 0)
            throw new InvalidOperationException($"{SectionPath}:{nameof(ClockSkewSeconds)} must not be negative.");
    }
}
