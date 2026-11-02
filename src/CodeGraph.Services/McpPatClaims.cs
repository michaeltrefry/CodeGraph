using System.Security.Claims;
using CodeGraph.Models.Work;

namespace CodeGraph.Services;

/// <summary>
/// The authentication type of a principal signed in with an MCP personal access token, and the
/// claims that mark an agent's token. Only an identity of that type is believed to be an agent.
/// </summary>
public static class McpPatClaims
{
    public const string Scheme = "McpPat";
    public const string ActorKind = "codegraph_actor_kind";
    public const string AgentId = "codegraph_agent_id";

    /// <summary>Whether <paramref name="user"/> signed in with an agent's token.</summary>
    public static bool IsAgent(ClaimsPrincipal? user) =>
        user?.Identities.Any(identity => identity.IsAuthenticated &&
            identity.AuthenticationType == Scheme &&
            identity.HasClaim(ActorKind, WorkActor.AgentKind)) == true;
}
