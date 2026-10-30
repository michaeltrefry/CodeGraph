using CodeGraph.Data;
using CodeGraph.Services.Indexer;
using Shouldly;

namespace CodeGraph.Jobs.Tests.Jobs;

public class IndexerRunRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cleanup_UsesOneCutoffAndContinuesUntilAShortBatch()
    {
        var store = new RetentionStore(5000, 5000, 12);
        var service = new IndexerRunRetentionService(store, new FixedTimeProvider());
        (await service.CleanupAsync()).ShouldBe(10012);
        store.Calls.Count.ShouldBe(3);
        store.Calls.ShouldAllBe(call => call.Cutoff == Now.UtcDateTime.AddDays(-30) && call.BatchSize == 5000);
    }

    [Fact]
    public async Task Cleanup_AppliesTheRequestedRetentionWindow()
    {
        var store = new RetentionStore(0);
        var service = new IndexerRunRetentionService(store, new FixedTimeProvider());
        (await service.CleanupAsync(7)).ShouldBe(0);
        store.Calls.Single().Cutoff.ShouldBe(Now.UtcDateTime.AddDays(-7));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Cleanup_RejectsInvalidDaysBeforeDeleting(int days)
    {
        var store = new RetentionStore(0);
        var service = new IndexerRunRetentionService(store, new FixedTimeProvider());
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => service.CleanupAsync(days));
        store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cleanup_ObservesCancellationBetweenFullBatches()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new RetentionStore(5000) { AfterDelete = cancellation.Cancel };
        var service = new IndexerRunRetentionService(store, new FixedTimeProvider());
        await Should.ThrowAsync<OperationCanceledException>(() => service.CleanupAsync(ct: cancellation.Token));
        store.Calls.Count.ShouldBe(1);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RetentionStore(params int[] batches) : IIndexerRunStore
    {
        private readonly Queue<int> _batches = new(batches);
        public List<(DateTime Cutoff, int BatchSize)> Calls { get; } = [];
        public Action? AfterDelete { get; init; }

        public Task<int> DeleteFinishedIndexerRunsBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default)
        {
            Calls.Add((cutoffUtc, batchSize));
            var deleted = _batches.Dequeue();
            AfterDelete?.Invoke();
            return Task.FromResult(deleted);
        }

        public Task<long> CreateIndexerRunAsync(IndexerRunEntity run, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateIndexerRunStatusAsync(long runId, string status, string? message = null, DateTime? completedAt = null, string? error = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerRunEntity?> GetIndexerRunAsync(long runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexerRunEntity>> ListIndexerRunsAsync(string? status = null, string? operation = null, int take = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerRunLease?> TryClaimNextIndexerRunAsync(string owner, DateTime now, DateTime leaseExpiresAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerRunLeaseRenewal> RenewIndexerRunLeaseAsync(long runId, string owner, long fencingToken, DateTime now, DateTime leaseExpiresAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CompleteIndexerRunAsync(IndexerRunLease lease, string message, DateTime completedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CompleteIndexerRunAsync(IndexerRunLease lease, string message, string? errorCode, string? error, DateTime completedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerRunFailureDisposition> FailOrRetryIndexerRunAsync(IndexerRunLease lease, string errorCode, string error, DateTime now, DateTime nextAttemptAt, int maxAttempts, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CancelOwnedIndexerRunAsync(IndexerRunLease lease, string message, DateTime completedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerRunEntity?> RequestIndexerRunCancellationAsync(long runId, DateTime requestedAt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
