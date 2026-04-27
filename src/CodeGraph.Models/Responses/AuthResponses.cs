namespace CodeGraph.Models.Responses;

public record AuthConfigResponse(
    bool Enabled,
    string Authority,
    string AuthorizationUrl,
    string TokenUrl,
    string EndSessionUrl,
    string ClientId,
    string Audience,
    string Scope);

public record CurrentUserResponse(
    string Username,
    bool IsAdmin);
