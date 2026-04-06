namespace TC.CodeGraphApi.Services.Configuration;

public class AuthOptions
{
    /// <summary>Used by Angular — OIDC authorize endpoint</summary>
    public string AuthorizationUrl { get; set; } = "https://stgauth.tcdevops.com/connect/authorize";

    /// <summary>Used by Angular — OIDC token endpoint</summary>
    public string TokenUrl { get; set; } = "https://stgauth.tcdevops.com/connect/token";

    /// <summary>Used by Angular — OIDC client ID</summary>
    public string ClientId { get; set; } = "codegraph-web";

    /// <summary>Used by API — OIDC discovery base URL for JWT validation</summary>
    public string Authority { get; set; } = "https://stgauth.tcdevops.com";
}
