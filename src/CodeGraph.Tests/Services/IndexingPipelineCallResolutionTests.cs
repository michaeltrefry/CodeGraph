using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Call resolution memoizes receiver ownership per receiver type and the owned-candidate filter per
/// (callee, receiver type). On the inference workspace the unmemoized loop issued one unindexed
/// store lookup per call and dominated the incremental re-index (57 s of a 59 s resolution phase).
/// These tests pin the output to the previous algorithm and bound the store traffic.
/// </summary>
public class IndexingPipelineCallResolutionTests
{
    private const string Project = "resolution";
    private static readonly string[] CalleeNames = ["new", "from", "get", "run", "fmt", "build", "len", "missing"];
    private static readonly NodeLabel[] OwnerLabels =
        [NodeLabel.Class, NodeLabel.Struct, NodeLabel.Interface, NodeLabel.Enum, NodeLabel.Module, NodeLabel.Namespace, NodeLabel.Record];

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public async Task ResolveCallsAsync_EmitsTheSameEdgesAsThePreviousAlgorithm(bool includePersistedNodes, int seed)
    {
        var graph = SyntheticGraph.Create(seed, ownerCount: 300, methodCount: 3_000, callCount: 20_000, persistedCount: 2_000);
        var store = graph.CreateStore();
        var expected = graph.CreateBuffer();
        var actual = graph.CreateBuffer();

        await new PreviousCallResolution(store).ResolveCallsAsync(
            Project, expected, includePersistedNodes, graph.ExcludedPersistedFiles, CancellationToken.None);
        await CreatePipeline(store).ResolveCallsAsync(
            Project, actual, includePersistedNodes, graph.ExcludedPersistedFiles, CancellationToken.None);

        var expectedCalls = CallEdges(expected);
        expectedCalls.Count.ShouldBeGreaterThan(300);
        CallEdges(actual).ShouldBe(expectedCalls);
    }

    [Fact]
    public async Task ResolveCallsAsync_LargeGraphLooksUpEachPersistedReceiverOnceAndFinishesQuickly()
    {
        // inference: 129k nodes, 916k pending edges, 67k unresolved calls before resolution.
        var graph = SyntheticGraph.Create(5, ownerCount: 4_000, methodCount: 96_000, callCount: 67_000, persistedCount: 20_000,
            existingCallEdges: 900_000);
        var store = graph.CreateStore();
        var buffer = graph.CreateBuffer();
        buffer.AllNodes.Count.ShouldBeGreaterThanOrEqualTo(100_000);
        buffer.AllPendingEdges.Count.ShouldBe(900_000);
        var resolvedReceiverTypes = buffer.AllUnresolvedCalls
            .Where(call => call.ReceiverKind == CallReceiverKind.Resolved && !string.IsNullOrWhiteSpace(call.ReceiverType))
            .Select(call => call.ReceiverType!)
            .ToHashSet(StringComparer.Ordinal);

        var stopwatch = Stopwatch.StartNew();
        await CreatePipeline(store).ResolveCallsAsync(
            Project, buffer, includePersistedNodes: true, graph.ExcludedPersistedFiles, CancellationToken.None);
        stopwatch.Stop();

        buffer.AllPendingEdges.Count.ShouldBeGreaterThan(900_000);
        store.QualifiedNameLookups.ShouldBeGreaterThan(0);
        store.QualifiedNameLookups.ShouldBeLessThanOrEqualTo(resolvedReceiverTypes.Count);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
    }

    private static IndexingPipeline CreatePipeline(IGraphStore store) =>
        new(store, [], Options.Create(new IndexingOptions()), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);

    private static List<(string Source, string Target, object? Confidence)> CallEdges(GraphBuffer buffer) =>
        buffer.AllPendingEdges
            .Where(edge => edge.Type == EdgeType.CALLS && edge.Properties is not null)
            .Select(edge => (edge.SourceQN, edge.TargetQN, edge.Properties!.GetValueOrDefault("confidence")))
            .ToList();

    private sealed class CountingGraphStore : InMemoryGraphStore
    {
        public int QualifiedNameLookups;

        public override Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName)
        {
            Interlocked.Increment(ref QualifiedNameLookups);
            return base.FindNodeByQualifiedNameAsync(project, qualifiedName);
        }
    }

    private sealed class SyntheticGraph
    {
        private readonly List<GraphNode> _nodes = [];
        private readonly List<GraphNode> _persisted = [];
        private readonly List<UnresolvedCall> _calls = [];
        private readonly List<PendingEdge> _edges = [];

        public IReadOnlySet<string> ExcludedPersistedFiles { get; } =
            new HashSet<string>(["src/changed_0.rs", "src/changed_1.rs"], StringComparer.OrdinalIgnoreCase);

        public static SyntheticGraph Create(
            int seed, int ownerCount, int methodCount, int callCount, int persistedCount, int existingCallEdges = 200)
        {
            var random = new Random(seed);
            var graph = new SyntheticGraph();
            var owners = new List<GraphNode>();
            for (var i = 0; i < ownerCount; i++)
            {
                // Half the owner names repeat across namespaces, so simple-name receivers are
                // ambiguous for those and resolve for the unique half.
                var name = i % 2 == 0 ? $"Unique{i}" : $"Owner{i % 10}";
                var owner = Node(OwnerLabels[random.Next(OwnerLabels.Length)], name, $"ns{i % 7}.{name}.{i}", "src/owners.rs");
                owners.Add(owner);
                graph._nodes.Add(owner);
            }

            // Non-owner nodes that share owner names exercise the owner-label filters.
            for (var i = 0; i < ownerCount / 10; i++)
                graph._nodes.Add(Node(NodeLabel.Method, $"Owner{i}", $"fns.Owner{i}#{i}", "src/fns.rs"));

            // About two methods per name: bare calls are sometimes unique, sometimes ambiguous.
            var nameSpread = Math.Max(1, methodCount / (CalleeNames.Length * 2));
            string MethodName() => $"{CalleeNames[random.Next(CalleeNames.Length - 1)]}{random.Next(nameSpread)}";

            var methods = new List<GraphNode>();
            var methodsByOwner = new Dictionary<GraphNode, List<GraphNode>>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < methodCount; i++)
            {
                var owner = owners[random.Next(owners.Count)];
                var name = MethodName();
                var method = random.Next(5) switch
                {
                    0 => Node(NodeLabel.Method, name, $"{owner.QualifiedName}.{name}.{i}", "src/methods.rs"),
                    1 => Node(NodeLabel.Method, name, $"{owner.QualifiedName}#method:{name}({i})", "src/methods.rs"),
                    // Owned only symbolically, through receiver_owner.
                    2 => Node(NodeLabel.Function, name, $"free{i % 13}.{name}.{i}", "src/free.rs",
                        new Dictionary<string, object> { ["receiver_owner"] = owner.Name }),
                    // Shares the owner's qualified-name prefix without a member separator: not owned.
                    3 => Node(NodeLabel.Method, name, $"{owner.QualifiedName}Sibling.{name}.{i}", "src/methods.rs"),
                    _ => Node(NodeLabel.Function, name, $"free{i % 13}.{name}.{i}", "src/free.rs")
                };
                methods.Add(method);
                graph._nodes.Add(method);
                if (!methodsByOwner.TryGetValue(owner, out var owned))
                    methodsByOwner[owner] = owned = [];
                owned.Add(method);
            }

            for (var i = 0; i < persistedCount; i++)
            {
                var file = i % 5 == 0 ? $"src/changed_{i % 2}.rs" : i % 7 == 0 ? "" : $"src/kept_{i % 50}.rs";
                if (i % 4 == 0)
                {
                    var name = random.Next(2) == 0 ? $"Owner{random.Next(10)}" : $"Unique{random.Next(ownerCount)}";
                    graph._persisted.Add(Node(OwnerLabels[random.Next(OwnerLabels.Length)], name, $"persisted.{name}.{i}", file));
                }
                else
                {
                    var owner = owners[random.Next(owners.Count)];
                    var name = MethodName();
                    graph._persisted.Add(Node(NodeLabel.Method, name, $"{owner.QualifiedName}.persisted.{name}.{i}", file));
                }
            }

            // Real workspaces repeat a modest set of receivers across many calls.
            var persistedOwners = graph._persisted.Where(node => OwnerLabels.Contains(node.Label)).ToList();
            var receivers = Enumerable.Range(0, 400)
                .Select(_ => random.Next(4) == 0
                    ? persistedOwners[random.Next(persistedOwners.Count)]
                    : owners[random.Next(owners.Count)])
                .ToList();

            string ReceiverType(GraphNode owner) => random.Next(8) switch
            {
                0 or 1 => owner.QualifiedName,
                2 or 3 => owner.Name,
                4 => $"crate::{owner.Name}",
                5 => $"::{owner.Name}::",
                6 => "Vec",
                _ => " "
            };

            var kinds = Enum.GetValues<CallReceiverKind>();
            for (var i = 0; i < callCount; i++)
            {
                var caller = methods[random.Next(methods.Count)].QualifiedName;
                var kind = kinds[random.Next(kinds.Length)];
                var callee = random.Next(50) == 0 ? "missing" : MethodName();
                string? receiverType = null;
                if (kind == CallReceiverKind.Resolved)
                {
                    var receiver = receivers[random.Next(receivers.Count)];
                    receiverType = ReceiverType(receiver);
                    // Mostly call something the receiver actually declares.
                    if (random.Next(4) != 0 && methodsByOwner.TryGetValue(receiver, out var declared))
                        callee = declared[random.Next(declared.Count)].Name;
                }

                graph._calls.Add(new UnresolvedCall(caller, callee, receiverType, Math.Round(random.NextDouble(), 2), kind));
            }

            // Pre-existing CALLS edges: resolution must not emit a duplicate of any of them.
            for (var i = 0; i < existingCallEdges; i++)
            {
                graph._edges.Add(new PendingEdge(
                    methods[random.Next(methods.Count)].QualifiedName,
                    methods[random.Next(methods.Count)].QualifiedName,
                    EdgeType.CALLS));
            }

            return graph;
        }

        public CountingGraphStore CreateStore()
        {
            var store = new CountingGraphStore();
            foreach (var node in _persisted)
                store.AddNode(node);
            return store;
        }

        public GraphBuffer CreateBuffer()
        {
            var buffer = new GraphBuffer();
            foreach (var node in _nodes)
                buffer.AddNode(node);
            foreach (var edge in _edges)
                buffer.AddEdge(edge);
            foreach (var call in _calls)
                buffer.AddUnresolvedCall(call);
            return buffer;
        }

        private static GraphNode Node(
            NodeLabel label, string name, string qualifiedName, string filePath, Dictionary<string, object>? properties = null) =>
            new()
            {
                Project = Project,
                Label = label,
                Name = name,
                QualifiedName = qualifiedName,
                FilePath = filePath,
                Properties = properties ?? []
            };
    }

    /// <summary>The call-resolution loop as it was before memoization, kept verbatim as the oracle.</summary>
    private sealed class PreviousCallResolution(IGraphStore store)
    {
        public async Task ResolveCallsAsync(
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
                    var persisted = await store.FindNodesByNameAsync(projectName, name);
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
                        FindCandidatesAsync);
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

        private async Task<GraphNode?> ResolveReceiverOwnerAsync(
            string projectName,
            GraphBuffer buffer,
            string receiverType,
            bool includePersistedNodes,
            Func<GraphNode, bool> isPersistedNodeEligible,
            Func<string, Task<IReadOnlyList<GraphNode>>> findCandidatesAsync)
        {
            var exact = buffer.FindByQN(receiverType);
            if (exact is not null && IsReceiverOwnerLabel(exact.Label))
                return exact;

            if (includePersistedNodes)
            {
                exact = await store.FindNodeByQualifiedNameAsync(projectName, receiverType);
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

        private static bool IsReceiverOwnerLabel(NodeLabel label) =>
            label is NodeLabel.Namespace or NodeLabel.Class or NodeLabel.Interface
                or NodeLabel.Enum or NodeLabel.Struct or NodeLabel.Record or NodeLabel.Module;

        private static bool IsOwnedBy(GraphNode candidate, GraphNode owner, bool allowSymbolicOwnership)
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

        private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').Trim('/');
    }
}
