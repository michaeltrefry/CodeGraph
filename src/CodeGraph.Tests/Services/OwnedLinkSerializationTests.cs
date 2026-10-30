using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class OwnedLinkSerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingTargetAdditionAndRemoval_UsesFreshSnapshotAfterOwnedLock(bool npm)
    {
        var store = new InterleavingStore(npm);
        foreach (var repository in new[] { "A", "B", "C" }) store.AddRepository(repository);
        var caller = store.AddNode(new GraphNode { Repository = "A", Label = NodeLabel.Component,
            Name = "App", QualifiedName = "A:App", FilePath = "ui/app.tsx" });
        var raw = store.AddNode(new GraphNode { Repository = "A", Label = npm ? NodeLabel.ExternalSymbol : NodeLabel.Route,
            Name = "raw", QualifiedName = npm ? "npm-symbol:@org/ui#Button" : "route:*:GET:/shared",
            Properties = npm ? new() { ["package_name"] = "@org/ui", ["import_path"] = "@org/ui", ["export_name"] = "Button" } : new() { ["stub"] = true } });
        store.AddEdge(new GraphEdge { Repository = "A", SourceId = caller, TargetId = raw,
            Type = npm ? EdgeType.RENDERS : EdgeType.HTTP_CALLS,
            Properties = npm ? [] : new() { ["http_method"] = "GET", ["url_pattern"] = "/shared" } });
        var provider = store.AddNode(Provider("C", npm));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await store.LinkForRepositoryAsync("C", timeout.Token);
        var unique = store.CrossEdges.ShouldHaveSingleItem();
        unique.SourceNodeId.ShouldBe(caller);
        unique.TargetNodeId.ShouldBe(provider);
        // A different relation is outside the owned HTTP/npm diff and must retain its identity.
        await store.InsertCrossRepoEdgeAsync(unique with { Id = 0, Type = EdgeType.IMPLEMENTS,
            Properties = new() { ["untouched"] = true } });
        var unrelated = store.CrossEdges.Single(edge => edge.Type == EdgeType.IMPLEMENTS);

        store.AddNode(Provider("B", npm));
        store.ArmSnapshotBarrier();
        var older = store.LinkForRepositoryAsync("B", timeout.Token);
        await store.SnapshotCaptured.Task.WaitAsync(timeout.Token);
        await store.DeleteNodesByFileAsync("B", "src/provider");
        store.ObserveNextOwnedLockAttempt = true;
        var newer = store.LinkForRepositoryAsync("B", timeout.Token);
        // Without serialization the newer run finishes here and the older stale snapshot then
        // deletes its repaired A→C edge. With serialization it cannot read a snapshot yet.
        await Task.WhenAny(newer, store.OwnedLockAttempted.Task).WaitAsync(timeout.Token);
        if (!newer.IsCompleted) store.BarrierSnapshots.ShouldBe(1);
        store.ReleaseSnapshot.TrySetResult();
        await Task.WhenAll(older, newer).WaitAsync(timeout.Token);
        store.BarrierSnapshots.ShouldBe(2);

        var repaired = store.CrossEdges.Single(edge => edge.Type == (npm ? EdgeType.RENDERS : EdgeType.HTTP_CALLS));
        repaired.SourceNodeId.ShouldBe(caller);
        repaired.TargetNodeId.ShouldBe(provider);
        repaired.TargetRepository.ShouldBe("C");
        store.CrossEdges.Single(edge => edge.Type == EdgeType.IMPLEMENTS).ShouldBe(unrelated);
        store.MaximumOwnedLocks.ShouldBe(1);
        store.OwnedLocksAtMessaging.ShouldAllBe(held => !held);
        var stableId = repaired.Id;
        await store.LinkForRepositoryAsync("B", timeout.Token);
        store.CrossEdges.Single(edge => edge.Type == repaired.Type).Id.ShouldBe(stableId);
    }

    private static GraphNode Provider(string repository, bool npm) => new()
    {
        Repository = repository, Label = npm ? NodeLabel.Component : NodeLabel.Route,
        Name = "Provider", QualifiedName = repository + ":Provider", FilePath = "src/provider",
        Properties = npm ? new() { ["npm_exports"] = JsonSerializer.Serialize(new[] {
            new { package_name = "@org/ui", import_path = "@org/ui", export_name = "Button" } }) }
            : new() { ["http_method"] = "GET", ["route_template"] = "/shared", ["route_kind"] = "endpoint" }
    };

    private sealed class InterleavingStore(bool npm) : InMemoryGraphStore, IGraphStore
    {
        private const string OwnedLock = CrossRepoLinker.OwnedLinksLockName;
        private bool _armed;
        private bool _trackSnapshots;
        private sealed class FlowState { public bool OwnsLock; }
        private readonly AsyncLocal<FlowState?> _flow = new();
        public async Task LinkForRepositoryAsync(string repository, CancellationToken ct)
        {
            _flow.Value = new();
            await new CrossRepoLinker(this, NullLogger<CrossRepoLinker>.Instance).LinkForRepositoryAsync(repository, ct);
        }
        private int _activeOwnedLocks;
        public bool ObserveNextOwnedLockAttempt { get; set; }
        public int MaximumOwnedLocks { get; private set; }
        public int BarrierSnapshots { get; private set; }
        public List<bool> OwnedLocksAtMessaging { get; } = [];
        public TaskCompletionSource SnapshotCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OwnedLockAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ArmSnapshotBarrier() { _armed = true; _trackSnapshots = true; BarrierSnapshots = 0; }

        async Task<IAsyncDisposable> IGraphStore.AcquireRepositoryIndexingLockAsync(string repository, CancellationToken ct)
        {
            if (repository != OwnedLock) return await base.AcquireRepositoryIndexingLockAsync(repository, ct);
            if (ObserveNextOwnedLockAttempt) OwnedLockAttempted.TrySetResult();
            var held = await base.AcquireRepositoryIndexingLockAsync(repository, ct);
            var active = Interlocked.Increment(ref _activeOwnedLocks);
            MaximumOwnedLocks = Math.Max(MaximumOwnedLocks, active);
            var flow = _flow.Value!;
            flow.OwnsLock = true;
            return new OwnedLease(held, () => { flow.OwnsLock = false; Interlocked.Decrement(ref _activeOwnedLocks); });
        }

        public override async IAsyncEnumerable<LinkNodeRow> StreamLinkNodesAsync(LinkNodeQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var matching = npm ? query.PropertyKeys.Contains("npm_exports") : query.Labels.Contains(NodeLabel.Route);
            var rows = new List<LinkNodeRow>();
            await foreach (var row in base.StreamLinkNodesAsync(query, ct)) rows.Add(row);
            if (matching && _trackSnapshots) BarrierSnapshots++;
            if (matching && _armed)
            {
                _armed = false;
                SnapshotCaptured.TrySetResult();
                await ReleaseSnapshot.Task.WaitAsync(ct);
            }
            foreach (var row in rows) yield return row;
        }

        public override async IAsyncEnumerable<LinkEdgeRow> StreamLinkEdgesAsync(LinkEdgeQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (query.Types.Count == 1 && query.Types[0] == EdgeType.PUBLISHES)
                OwnedLocksAtMessaging.Add(_flow.Value?.OwnsLock == true);
            await foreach (var row in base.StreamLinkEdgesAsync(query, ct)) yield return row;
        }

        private sealed class OwnedLease(IAsyncDisposable inner, Action released) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                released();
                await inner.DisposeAsync();
            }
        }
    }
}
