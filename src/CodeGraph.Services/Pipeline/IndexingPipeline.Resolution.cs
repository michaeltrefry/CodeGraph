using Microsoft.Extensions.Logging;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    private static readonly string[] ExternalNamespacePrefixes =
    [
        "System.",
        "Microsoft.",
        "Newtonsoft.",
        "MassTransit.",
        "Autofac.",
        "Serilog.",
        "Npgsql.",
        "RabbitMQ.",
        "Neo4j.",
        "Anthropic."
    ];

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
    private async Task ResolveCallsAsync(
        string projectName,
        GraphBuffer buffer,
        bool includePersistedNodes,
        IReadOnlySet<string> excludedPersistedFiles,
        CancellationToken ct)
    {
        var emittedCallEdges = buffer.AllPendingEdges
            .Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => $"{edge.SourceQN}\u001f{edge.TargetQN}")
            .ToHashSet(StringComparer.Ordinal);
        var nodesByName = new Dictionary<string, IReadOnlyList<GraphNode>>(StringComparer.Ordinal);

        async Task<IReadOnlyList<GraphNode>> FindCandidatesAsync(string name)
        {
            if (nodesByName.TryGetValue(name, out var cached))
                return cached;

            var candidates = buffer.FindByName(name).AsEnumerable();
            if (includePersistedNodes)
            {
                var persisted = await _store.FindNodesByNameAsync(projectName, name);
                candidates = candidates.Concat(persisted.Where(IsPersistedNodeEligible));
            }

            cached = candidates
                .DistinctBy(node => node.QualifiedName, StringComparer.Ordinal)
                .ToList();
            nodesByName[name] = cached;
            return cached;
        }

        bool IsPersistedNodeEligible(GraphNode node) =>
            string.IsNullOrWhiteSpace(node.FilePath)
            || !excludedPersistedFiles.Contains(NormalizeRelativePath(node.FilePath));

        foreach (var call in buffer.AllUnresolvedCalls)
        {
            ct.ThrowIfCancellationRequested();
            if (call.ReceiverKind is CallReceiverKind.Unknown or CallReceiverKind.Unresolved)
                continue;

            var candidates = await FindCandidatesAsync(call.CalleeName);
            if (call.ReceiverKind == CallReceiverKind.Resolved)
            {
                if (string.IsNullOrWhiteSpace(call.ReceiverType))
                    continue;

                var receiver = await ResolveReceiverOwnerAsync(
                    projectName,
                    buffer,
                    call.ReceiverType,
                    includePersistedNodes,
                    IsPersistedNodeEligible,
                    FindCandidatesAsync,
                    ct);
                if (receiver is null)
                    continue;

                var receiverOwners = (await FindCandidatesAsync(receiver.Name))
                    .Where(node => IsReceiverOwnerLabel(node.Label))
                    .ToList();
                var allowSymbolicOwnership = receiverOwners.Count == 1
                    && string.Equals(
                        receiverOwners[0].QualifiedName,
                        receiver.QualifiedName,
                        StringComparison.Ordinal);
                candidates = candidates
                    .Where(candidate => IsOwnedBy(candidate, receiver, allowSymbolicOwnership))
                    .ToList();
            }

            if (candidates.Count == 1)
            {
                var edgeKey = $"{call.CallerQN}\u001f{candidates[0].QualifiedName}";
                if (!emittedCallEdges.Add(edgeKey))
                    continue;

                buffer.AddEdge(new PendingEdge(
                    call.CallerQN,
                    candidates[0].QualifiedName,
                    EdgeType.CALLS,
                    new Dictionary<string, object> { ["confidence"] = call.Confidence }));
            }
        }
    }

    private static bool IsReceiverOwnerLabel(NodeLabel label) =>
        label is NodeLabel.Namespace or NodeLabel.Class or NodeLabel.Interface
            or NodeLabel.Enum or NodeLabel.Struct or NodeLabel.Record or NodeLabel.Module;

    private async Task<GraphNode?> ResolveReceiverOwnerAsync(
        string projectName,
        GraphBuffer buffer,
        string receiverType,
        bool includePersistedNodes,
        Func<GraphNode, bool> isPersistedNodeEligible,
        Func<string, Task<IReadOnlyList<GraphNode>>> findCandidatesAsync,
        CancellationToken ct)
    {
        var exact = buffer.FindByQN(receiverType);
        if (exact is not null && IsReceiverOwnerLabel(exact.Label))
            return exact;

        if (includePersistedNodes)
        {
            exact = await _store.FindNodeByQualifiedNameAsync(projectName, receiverType);
            if (exact is not null && isPersistedNodeEligible(exact) && IsReceiverOwnerLabel(exact.Label))
                return exact;
        }

        var normalized = receiverType
            .Replace("::", ".", StringComparison.Ordinal)
            .Trim('.');
        if (normalized.Contains('.', StringComparison.Ordinal))
            return null;

        var simpleName = normalized[(normalized.LastIndexOf('.') + 1)..];
        var matches = (await findCandidatesAsync(simpleName))
            .Where(node => IsReceiverOwnerLabel(node.Label))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsOwnedBy(
        GraphNode candidate,
        GraphNode owner,
        bool allowSymbolicOwnership)
    {
        if (candidate.QualifiedName.StartsWith(owner.QualifiedName + ".", StringComparison.Ordinal)
            || candidate.QualifiedName.StartsWith(owner.QualifiedName + "#method:", StringComparison.Ordinal))
        {
            return true;
        }

        return allowSymbolicOwnership
            && candidate.Properties.TryGetValue("receiver_owner", out var receiverOwner)
            && string.Equals(receiverOwner?.ToString(), owner.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// For edges whose target doesn't exist in the buffer, create stub nodes so the edges
    /// survive resolution and reach the database. For cross-repo edge types (PUBLISHES,
    /// CONSUMES, HTTP_CALLS, etc.) all missing targets get stubs. For CALLS, INJECTS,
    /// INHERITS, and IMPLEMENTS, only targets that look like application types get stubs
    /// (to avoid creating stubs for framework/System types).
    /// </summary>
    private void CreateStubNodesForExternalTargets(string projectName, GraphBuffer buffer)
    {
        // These edge types always get stubs for missing targets
        var alwaysStubEdgeTypes = new HashSet<EdgeType>
        {
            EdgeType.PUBLISHES, EdgeType.CONSUMES, EdgeType.HTTP_CALLS,
            EdgeType.ROUTED_TO, EdgeType.BOUND_TO, EdgeType.REGISTERS
        };

        // These edge types only get stubs when the target looks like an internal type
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

            if (isConditionalStub && !LooksLikeApplicationType(pending.TargetQN))
                continue;

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

    private static bool LooksLikeApplicationType(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName) || !qualifiedName.Contains('.'))
            return false;

        return !ExternalNamespacePrefixes.Any(prefix =>
            qualifiedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
