using TC.Common.TcServiceStack.Queue.Abstractions;

namespace TC.CodeGraphApi.Models.Messages;

/// <summary>
/// Published when a repository is no longer found in GitLab during discovery.
/// Triggers cascading cleanup of nodes, edges, analysis records, and cross-repo links.
/// </summary>
[TcServiceBusEvent(TcQueueHosts.Enterprise)]
public class RepositoryRemoved
{
    public string Name { get; set; } = "";
}
