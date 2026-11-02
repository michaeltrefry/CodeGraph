namespace CodeGraph.Data;

/// <summary>
/// The agents (named non-human identities) and the facts their management rules need. Deleted
/// agents are never answered by the reads.
/// </summary>
public interface IAgentStore
{
    Task<IReadOnlyList<AgentEntity>> ListAgentsAsync(CancellationToken ct = default);

    Task<AgentEntity?> GetAgentAsync(long id, CancellationToken ct = default);

    /// <summary>Stores a new agent, or answers null when an agent that is not deleted has its name.</summary>
    Task<AgentEntity?> CreateAgentAsync(AgentEntity agent, CancellationToken ct = default);

    /// <summary>Saves the name and disabled state, or answers false when another agent has the name.</summary>
    Task<bool> UpdateAgentAsync(AgentEntity agent, CancellationToken ct = default);

    /// <summary>Marks the agent deleted and revokes every token of it in one transaction.</summary>
    Task<bool> DeleteAgentAsync(long id, DateTime deletedAt, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="name"/> is a person CodeGraph has recorded: an admin, the owner of a
    /// person's token, a person's session, or a person's write in the change feed.
    /// </summary>
    Task<bool> IsHumanIdentityAsync(string name, CancellationToken ct = default);

    /// <summary>Whether a session of <paramref name="name"/> is open and was seen after <paramref name="seenAfter"/>.</summary>
    Task<bool> HasLiveSessionAsync(string name, DateTime seenAfter, CancellationToken ct = default);
}
