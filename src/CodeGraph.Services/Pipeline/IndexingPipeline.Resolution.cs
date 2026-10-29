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
    /// Resolve import statements to namespace/type nodes by qualified name. Swift imports name
    /// modules, not nodes, and are resolved by <see cref="ResolveSwiftImportsAsync"/>.
    /// </summary>
    private void ResolveImports(GraphBuffer buffer)
    {
        foreach (var import in buffer.AllUnresolvedImports)
        {
            if (import.FileQN.EndsWith(".swift", StringComparison.OrdinalIgnoreCase))
                continue;

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
    internal async Task ResolveCallsAsync(
        string repositoryName,
        GraphBuffer buffer,
        bool includePersistedNodes,
        IReadOnlySet<string> excludedPersistedFiles,
        CancellationToken ct,
        bool swiftManifestsExtracted = true,
        SwiftRebinding? swiftImportRebinding = null)
    {
        // Swift calls bind within the caller's module and the modules its file imports; the
        // scopes are loaded only when a Swift caller has candidates to filter.
        SwiftModuleScopes? swiftScopes = null;
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
                var persisted = await _store.FindNodesByNameAsync(repositoryName, name);
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

        // Every lookup below is a pure function of its key for the duration of this loop: the
        // loop only adds edges (never nodes) to the buffer and the store is not written. Memoizing
        // per receiver type and per (callee, receiver type) therefore yields the same edges while
        // replacing one store round trip and one O(candidates) ownership scan per call with one
        // per distinct key — large Rust workspaces repeat the same receivers tens of thousands
        // of times.
        var receiversByType = new Dictionary<string, ReceiverOwnership?>(StringComparer.Ordinal);
        var ownedCandidates = new Dictionary<(string CalleeName, string ReceiverType), IReadOnlyList<GraphNode>>();

        async Task<ReceiverOwnership?> FindReceiverAsync(string receiverType)
        {
            if (receiversByType.TryGetValue(receiverType, out var cached))
                return cached;

            // A Swift receiver names a type by its dotted path: it owns the members declared in
            // the type at that path and in extensions of that path, even when the type itself is
            // declared outside the repository.
            if (SwiftTypeReference.TryParse(receiverType, out var swiftPath))
            {
                var (type, ambiguous) = await ResolveSwiftReceiverAsync(swiftPath, FindCandidatesAsync);
                cached = new ReceiverOwnership(type, false, swiftPath, ambiguous);
                receiversByType[receiverType] = cached;
                return cached;
            }

            var receiver = await ResolveReceiverOwnerAsync(
                repositoryName,
                buffer,
                receiverType,
                includePersistedNodes,
                IsPersistedNodeEligible,
                FindCandidatesAsync,
                ct);
            if (receiver is not null)
            {
                var receiverOwners = (await FindCandidatesAsync(receiver.Name))
                    .Where(node => IsReceiverOwnerLabel(node.Label))
                    .ToList();
                var allowSymbolicOwnership = receiverOwners.Count == 1
                    && string.Equals(
                        receiverOwners[0].QualifiedName,
                        receiver.QualifiedName,
                        StringComparison.Ordinal);
                cached = new ReceiverOwnership(receiver, allowSymbolicOwnership);
            }

            receiversByType[receiverType] = cached;
            return cached;
        }

        foreach (var call in buffer.AllUnresolvedCalls)
        {
            ct.ThrowIfCancellationRequested();
            if (call.ReceiverKind is CallReceiverKind.Unknown or CallReceiverKind.Unresolved)
                continue;

            // A caller missing from the buffer still has a file: the one its qualified name is
            // declared in.
            var callerFile = buffer.FindByQN(call.CallerQN) is { FilePath: { Length: > 0 } callerPath }
                ? NormalizeRelativePath(callerPath)
                : CallLanguages.FileOfQualifiedName(repositoryName, call.CallerQN);
            var callerFamily = CallLanguages.FamilyOf(callerFile);

            // Module visibility only breaks ties for a Swift caller (several candidates, or a
            // receiver type several modules declare); the scopes are loaded on first need.
            var isSwiftCaller = callerFamily == "swift" && callerFile is not null;
            var callerFileQN = $"{repositoryName}:{callerFile}";
            async Task<SwiftModuleScopes?> ScopesAsync()
            {
                swiftScopes ??= await LoadSwiftModuleScopesAsync(
                    repositoryName, buffer, swiftManifestsExtracted, includePersistedNodes, excludedPersistedFiles,
                    swiftImportRebinding ?? SwiftRebinding.None, ct);
                return swiftScopes.IsEmpty ? null : swiftScopes;
            }

            var candidates = await FindCandidatesAsync(call.CalleeName);
            if (call.ReceiverKind == CallReceiverKind.Resolved)
            {
                if (string.IsNullOrWhiteSpace(call.ReceiverType))
                    continue;

                var ownership = await FindReceiverAsync(call.ReceiverType);
                if (ownership is null)
                    continue;

                var key = (call.CalleeName, call.ReceiverType);
                // A Swift type path several modules declare (a vendored copy) names the one the
                // caller can see.
                if (ownership is { SwiftPath: { } ambiguousPath, Ambiguous: true } && isSwiftCaller
                    && await ScopesAsync() is { } scopes)
                {
                    var callerScope = scopes.ScopeOf(callerFile);
                    var (visibleType, _) = await ResolveSwiftReceiverAsync(
                        ambiguousPath, FindCandidatesAsync,
                        type => scopes.IsVisible(callerScope, callerFileQN, type.FilePath));
                    ownership = ownership with { Receiver = visibleType };
                    key = (call.CalleeName, $"{call.ReceiverType}\u001f{visibleType?.QualifiedName}");
                }

                if (!ownedCandidates.TryGetValue(key, out var owned))
                {
                    owned = candidates
                        .Where(candidate => ownership.SwiftPath is { } path
                            ? IsOwnedBySwiftType(repositoryName, candidate, ownership.Receiver, path)
                            : IsOwnedBy(candidate, ownership.Receiver!, ownership.AllowSymbolicOwnership))
                        .ToList();
                    ownedCandidates[key] = owned;
                }

                candidates = owned;
            }

            // A name binds only to a callable (or a same-family type it constructs), within the
            // caller's language family, and across real FFI boundaries only from a bare call to a
            // free function the boundary exposes.
            candidates = candidates
                .Where(candidate => CallLanguages.IsCallTarget(
                    callerFamily, candidate, isBareCall: call.ReceiverKind == CallReceiverKind.Bare))
                .ToList();

            if (call.ArgumentLabels is not null)
                candidates = ArgumentLabels.Select(candidates, call.ArgumentLabels);

            // A Swift name several definitions share binds to the one the caller can see: the
            // single candidate of its own module, else the single one of the modules it imports
            // (re-exports included). A single candidate binds as it is; a remaining tie stays
            // unbound.
            if (isSwiftCaller && candidates.Count > 1 && await ScopesAsync() is { } swiftScopesForTie)
                candidates = PreferVisibleSwiftCandidates(swiftScopesForTie, callerFile!, callerFileQN, candidates);

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

    /// <summary>
    /// The tie-break of several Swift call candidates: the single Swift candidate of the caller's
    /// own module, else the single Swift candidate of the modules the caller's file sees through its
    /// imports; otherwise the candidates unchanged (still ambiguous).
    /// </summary>
    private static IReadOnlyList<GraphNode> PreferVisibleSwiftCandidates(
        SwiftModuleScopes scopes, string callerFile, string callerFileQN, IReadOnlyList<GraphNode> candidates)
    {
        if (scopes.ScopeOf(callerFile) is not { } callerScope)
            return candidates;

        var swift = candidates
            .Where(candidate => CallLanguages.FamilyOf(candidate.FilePath) == "swift")
            .Select(candidate => (Candidate: candidate, Scope: scopes.ScopeOf(candidate.FilePath)))
            .ToList();
        var own = swift.Where(entry => entry.Scope == callerScope).ToList();
        if (own.Count == 1)
            return [own[0].Candidate];
        if (own.Count > 1)
            return candidates;

        var imported = scopes.ImportedScopesOf(callerFileQN);
        var visible = swift.Where(entry => entry.Scope is { } scope && imported.Contains(scope)).ToList();
        return visible.Count == 1 ? [visible[0].Candidate] : candidates;
    }

    private sealed record ReceiverOwnership(
        GraphNode? Receiver, bool AllowSymbolicOwnership, string? SwiftPath = null, bool Ambiguous = false);

    /// <summary>
    /// The Swift type at <paramref name="typePath"/>: the single repository type at that path
    /// (<see cref="SwiftTypeReference.TypePath"/>) among those <paramref name="isVisible"/> accepts
    /// (all when null). An unqualified name that no top-level type declares falls back to the
    /// single Swift type of that simple name (a nested type named from inside its parent's scope).
    /// Null when it is ambiguous (<c>Ambiguous</c>) or not declared in the repository.
    /// </summary>
    private static async Task<(GraphNode? Type, bool Ambiguous)> ResolveSwiftReceiverAsync(
        string typePath,
        Func<string, Task<IReadOnlyList<GraphNode>>> findCandidatesAsync,
        Func<GraphNode, bool>? isVisible = null)
    {
        var types = (await findCandidatesAsync(SwiftTypeReference.SimpleName(typePath)))
            .Where(IsSwiftTypeNode)
            .Where(node => isVisible?.Invoke(node) != false)
            .ToList();
        var atPath = types
            .Where(node => SwiftTypeReference.TypePath(node.Repository, node.FilePath, node.QualifiedName) == typePath)
            .ToList();
        if (atPath.Count > 0)
            return atPath.Count == 1 ? (atPath[0], false) : (null, true);

        if (typePath.Contains('.', StringComparison.Ordinal))
            return (null, false);
        return types.Count == 1 ? (types[0], false) : (null, types.Count > 1);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a member of the Swift type at <paramref name="typePath"/>:
    /// declared in the type's body (<paramref name="owner"/>), or in an extension of that path in
    /// any file (<c>{file}#extension:{path}#method:...</c>).
    /// </summary>
    private static bool IsOwnedBySwiftType(string repositoryName, GraphNode candidate, GraphNode? owner, string typePath)
    {
        if (owner is not null && IsOwnedBy(candidate, owner, allowSymbolicOwnership: false))
            return true;

        return !string.IsNullOrEmpty(candidate.FilePath)
            && candidate.QualifiedName.StartsWith(
                $"{repositoryName}:{candidate.FilePath}#extension:{typePath}#method:", StringComparison.Ordinal);
    }

    private static bool IsReceiverOwnerLabel(NodeLabel label) =>
        label is NodeLabel.Namespace or NodeLabel.Class or NodeLabel.Interface
            or NodeLabel.Enum or NodeLabel.Struct or NodeLabel.Record or NodeLabel.Module;

    private async Task<GraphNode?> ResolveReceiverOwnerAsync(
        string repositoryName,
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
            exact = await _store.FindNodeByQualifiedNameAsync(repositoryName, receiverType);
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
        // Equivalent to StartsWith(owner + ".") || StartsWith(owner + "#method:") without
        // allocating both prefixes for every candidate.
        var qualifiedName = candidate.QualifiedName.AsSpan();
        var ownerName = owner.QualifiedName;
        if (qualifiedName.StartsWith(ownerName, StringComparison.Ordinal))
        {
            var rest = qualifiedName[ownerName.Length..];
            if (rest.StartsWith(".", StringComparison.Ordinal)
                || rest.StartsWith("#method:", StringComparison.Ordinal))
            {
                return true;
            }
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
    private void CreateStubNodesForExternalTargets(string repositoryName, GraphBuffer buffer)
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
                Repository = repositoryName,
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
