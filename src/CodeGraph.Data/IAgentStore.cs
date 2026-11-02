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

    /// <summary>
    /// Saves the name and disabled state. A rename (<paramref name="previousName"/> set) runs under
    /// the agent row's lock: refused while a session of the previous name was seen after
    /// <paramref name="liveAfter"/>, and otherwise ends that name's idle sessions, so another
    /// agent given the name later cannot resume them (sessions are bound by name).
    /// </summary>
    Task<AgentWriteOutcome> UpdateAgentAsync(AgentEntity agent, string? previousName, DateTime liveAfter, CancellationToken ct = default);

    /// <summary>
    /// Marks the agent deleted, revokes its tokens and ends its idle sessions in one transaction
    /// under the agent row's lock; refused while one of its sessions was seen after <paramref name="liveAfter"/>.
    /// </summary>
    Task<AgentWriteOutcome> DeleteAgentAsync(long id, DateTime deletedAt, DateTime liveAfter, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="name"/> is a person CodeGraph has recorded: an admin, the owner of a
    /// person's token, a person's session, or a person's write in the change feed.
    /// </summary>
    Task<bool> IsHumanIdentityAsync(string name, CancellationToken ct = default);

}

public enum AgentWriteOutcome
{
    Done,
    NotFound,
    NameTaken,
    LiveSession
}
