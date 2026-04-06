using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services.Pipeline;

public partial class IndexingPipeline
{
    /// <summary>
    /// Resolve import statements to namespace/type nodes.
    /// Phase 2 stub — full resolution happens when extractors populate UnresolvedImports.
    /// </summary>
    private void ResolveImports(GraphBuffer buffer)
    {
        foreach (var import in buffer.AllUnresolvedImports)
        {
            var target = buffer.FindByQN(import.ImportedNamespace);
            if (target != null)
            {
                buffer.AddEdge(new PendingEdge(
                    import.FileQN,
                    target.QualifiedName,
                    EdgeType.IMPORTS));
            }
        }
    }

    /// <summary>
    /// Resolve method calls to target method nodes.
    /// Phase 2 stub — full resolution happens when extractors populate UnresolvedCalls.
    /// </summary>
    private void ResolveCalls(GraphBuffer buffer)
    {
        foreach (var call in buffer.AllUnresolvedCalls)
        {
            // Try to find by qualified receiver type + method name
            if (call.ReceiverType != null)
            {
                var candidates = buffer.FindByName(call.CalleeName)
                    .Where(n => n.QualifiedName.StartsWith(call.ReceiverType))
                    .ToList();

                if (candidates.Count == 1)
                {
                    buffer.AddEdge(new PendingEdge(
                        call.CallerQN,
                        candidates[0].QualifiedName,
                        EdgeType.CALLS,
                        new Dictionary<string, object> { ["confidence"] = call.Confidence }));
                }
            }
        }
    }

    /// <summary>
    /// For edges whose target doesn't exist in the buffer, create stub nodes so the edges
    /// survive resolution and reach the database. For cross-repo edge types (PUBLISHES,
    /// CONSUMES, HTTP_CALLS, etc.) all missing targets get stubs. For CALLS, INJECTS,
    /// INHERITS, and IMPLEMENTS, only targets in TC.* namespaces get stubs (to avoid
    /// creating stubs for framework/System types).
    /// </summary>
    private void CreateStubNodesForExternalTargets(string projectName, GraphBuffer buffer)
    {
        // These edge types always get stubs for missing targets
        var alwaysStubEdgeTypes = new HashSet<EdgeType>
        {
            EdgeType.PUBLISHES, EdgeType.CONSUMES, EdgeType.HTTP_CALLS,
            EdgeType.DEPLOYS, EdgeType.CONFIGURES,
            EdgeType.ROUTED_TO, EdgeType.BOUND_TO, EdgeType.REGISTERS
        };

        // These edge types only get stubs when the target looks like an internal type (TC.*)
        var conditionalStubEdgeTypes = new HashSet<EdgeType>
        {
            EdgeType.CALLS, EdgeType.INJECTS, EdgeType.INHERITS,
            EdgeType.IMPLEMENTS, EdgeType.CARRIES_FIELD
        };

        var stubCount = 0;
        foreach (var pending in buffer.AllPendingEdges)
        {
            var isAlwaysStub = alwaysStubEdgeTypes.Contains(pending.Type);
            var isConditionalStub = conditionalStubEdgeTypes.Contains(pending.Type);

            if (!isAlwaysStub && !isConditionalStub)
                continue;

            // If target already exists in the buffer, no stub needed
            if (buffer.FindByQN(pending.TargetQN) is not null)
                continue;

            // For conditional types, only create stubs for internal (TC.*) targets
            if (isConditionalStub)
            {
                if (!pending.TargetQN.StartsWith("TC."))
                    continue;
            }

            var (label, name) = pending.Type switch
            {
                EdgeType.HTTP_CALLS => (NodeLabel.Route, pending.TargetQN),
                EdgeType.PUBLISHES or EdgeType.CONSUMES => (NodeLabel.Event,
                    pending.TargetQN.Contains('.')
                        ? pending.TargetQN[(pending.TargetQN.LastIndexOf('.') + 1)..]
                        : pending.TargetQN),
                EdgeType.ROUTED_TO => (NodeLabel.Queue, pending.TargetQN),
                EdgeType.BOUND_TO => (NodeLabel.Exchange, pending.TargetQN),
                EdgeType.REGISTERS => (NodeLabel.Class, pending.TargetQN.Contains('.')
                    ? pending.TargetQN[(pending.TargetQN.LastIndexOf('.') + 1)..]
                    : pending.TargetQN),
                EdgeType.DEPLOYS => (NodeLabel.Service, pending.TargetQN),
                EdgeType.CONFIGURES => (NodeLabel.Service, pending.TargetQN),
                EdgeType.INCLUDES_MODULE => (NodeLabel.TerraformModule, pending.TargetQN),
                EdgeType.DEPENDS_ON => (NodeLabel.TerraformResource, pending.TargetQN),
                EdgeType.INJECTS => (NodeLabel.Interface, pending.TargetQN.Contains('.')
                    ? pending.TargetQN[(pending.TargetQN.LastIndexOf('.') + 1)..]
                    : pending.TargetQN),
                EdgeType.INHERITS => (NodeLabel.Class, pending.TargetQN.Contains('.')
                    ? pending.TargetQN[(pending.TargetQN.LastIndexOf('.') + 1)..]
                    : pending.TargetQN),
                EdgeType.IMPLEMENTS => (NodeLabel.Interface, pending.TargetQN.Contains('.')
                    ? pending.TargetQN[(pending.TargetQN.LastIndexOf('.') + 1)..]
                    : pending.TargetQN),
                EdgeType.CALLS => (NodeLabel.Method, pending.TargetQN.Contains('.')
                    ? pending.TargetQN[(pending.TargetQN.LastIndexOf('.') + 1)..]
                    : pending.TargetQN),
                EdgeType.CARRIES_FIELD => (NodeLabel.Class, pending.TargetQN.Contains('.')
                    ? pending.TargetQN[(pending.TargetQN.LastIndexOf('.') + 1)..]
                    : pending.TargetQN),
                _ => (NodeLabel.Class, pending.TargetQN)
            };

            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                Label = label,
                Name = name,
                QualifiedName = pending.TargetQN,
                Properties = new() { ["stub"] = true }
            });
            stubCount++;
        }

        if (stubCount > 0)
            _logger.LogInformation("Created {Count} stub node(s) for external edge targets", stubCount);
    }
}
