using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Indexer;

public sealed class IndexerRunWorkerOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// Executes durable indexer runs. ProcessRepository runs (one repository's index, which can take
/// hours) execute in their own lane of <see cref="IndexingOptions.MaxParallelRepos"/> workers, so they
/// neither wait behind nor block the single lane that runs every other operation.
/// </summary>
public sealed class IndexerRunBackgroundRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<IndexerRunWorkerOptions> optionsAccessor,
    ILogger<IndexerRunBackgroundRunner> logger,
    IOptions<IndexingOptions>? indexingOptions = null,
    TimeProvider? timeProvider = null) : BackgroundService, IIndexerRunBackgroundRunner
{
    internal static readonly IndexerRunOperationFilter RepositoryLane =
        new(IndexerRunOperations.ProcessRepository, Include: true);
    internal static readonly IndexerRunOperationFilter GeneralLane =
        new(IndexerRunOperations.ProcessRepository, Include: false);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private volatile SemaphoreSlim[] _wakes = [];

    public Task EnqueueAsync(long runId, CancellationToken ct = default)
    {
        foreach (var wake in _wakes)
        {
            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wake-up is already pending; persisted work will be drained in order.
            }
        }
        return Task.CompletedTask;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var repositoryWorkers = Math.Max(1, indexingOptions?.Value.MaxParallelRepos ?? 1);
        var lanes = new[] { GeneralLane }
            .Concat(Enumerable.Repeat(RepositoryLane, repositoryWorkers))
            .ToArray();
        var wakes = lanes.Select(_ => new SemaphoreSlim(0, 1)).ToArray();
        _wakes = wakes;
        return Task.WhenAll(lanes.Select((lane, index) => RunLaneAsync(lane, wakes[index], stoppingToken)));
    }

    private async Task RunLaneAsync(IndexerRunOperationFilter lane, SemaphoreSlim wake, CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await TryExecuteNextAsync(stoppingToken, lane))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Durable indexer worker failed while polling for work");
            }

            try
            {
                await wake.WaitAsync(optionsAccessor.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Claims and executes one run; <paramref name="lane"/> null claims any run.</summary>
    internal async Task<bool> TryExecuteNextAsync(
        CancellationToken stoppingToken,
        IndexerRunOperationFilter? lane = null)
    {
        var options = optionsAccessor.Value;
        ValidateOptions(options);

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIndexerRunStore>();
        var now = UtcNow;
        var lease = lane is null
            ? await store.TryClaimNextIndexerRunAsync(
                _owner,
                now,
                now + options.LeaseDuration,
                options.MaxAttempts,
                stoppingToken)
            : await store.TryClaimNextIndexerRunAsync(
                _owner,
                now,
                now + options.LeaseDuration,
                options.MaxAttempts,
                lane,
                stoppingToken);
        if (lease is null)
            return false;

        var started = _time.GetTimestamp();
        logger.LogInformation(
            "Indexer run {RunId} started: {Operation} {Target} (attempt {Attempt})",
            lease.Run.Id, lease.Run.Operation, lease.Run.Target, lease.Run.AttemptCount);

        var executor = scope.ServiceProvider.GetRequiredService<IndexerRunExecutor>();
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var execution = executor.ExecuteAsync(lease, executionCancellation.Token);
        var monitor = MonitorLeaseAsync(lease, options, monitorCancellation.Token);

        try
        {
            var winner = await Task.WhenAny(execution, monitor);
            if (winner == monitor)
            {
                var leaseState = await monitor;
                executionCancellation.Cancel();
                try
                {
                    await execution;
                }
                catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
                {
                    // Expected when cancellation or lease loss reaches cooperative work.
                }

                if (leaseState == LeaseMonitorResult.CancellationRequested)
                {
                    await store.CancelOwnedIndexerRunAsync(
                        lease,
                        "Canceled by request while the operation was running.",
                        UtcNow,
                        CancellationToken.None);
                }
                else
                {
                    logger.LogWarning(
                        "Indexer run {RunId} lost lease ownership; fencing token {FencingToken} prevents stale completion",
                        lease.Run.Id,
                        lease.FencingToken);
                }

                return true;
            }

            var message = await execution;
            monitorCancellation.Cancel();
            await IgnoreMonitorCancellationAsync(monitor);
            var diagnostics = scope.ServiceProvider.GetService<IndexingRunDiagnostics>();
            var (degradationCode, degradation, degradationSummary) = DescribeDegradations(diagnostics);
            // The full per-group record lives in repositories.properties.rustSemanticOutcomes;
            // the run message keeps a short per-run summary.
            if (diagnostics?.RustSemanticOutcomes is { } rustOutcomes)
                message = $"{message} {rustOutcomes.Summary()}";
            if (degradationSummary is not null)
                message = $"{message} {degradationSummary}";
            if (!await store.CompleteIndexerRunAsync(
                    lease, message, degradationCode, degradation, UtcNow, CancellationToken.None))
            {
                logger.LogWarning(
                    "Indexer run {RunId} completed work after losing lease; terminal update was fenced out",
                    lease.Run.Id);
            }
            else
            {
                logger.LogInformation(
                    "Indexer run {RunId} finished: {Operation} {Target} completed in {Elapsed}",
                    lease.Run.Id, lease.Run.Operation, lease.Run.Target, _time.GetElapsedTime(started));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Leave the durable lease in place. A retry-safe run will be recovered after
            // expiry; an ambiguous non-idempotent run will be failed without replay.
            throw;
        }
        catch (Exception ex)
        {
            executionCancellation.Cancel();
            monitorCancellation.Cancel();
            await IgnoreMonitorCancellationAsync(monitor);
            var errorCode = ex switch
            {
                RustSemanticIndexingException rustFailure => rustFailure.FailureCode,
                BranchOverlayIndexingException => BranchOverlayIndexingException.FailureCode,
                _ => "indexer_operation_failed"
            };
            // Semantic extractor failures are deterministic for the current checkout and
            // toolchain. Replaying a CPU- and memory-intensive command immediately only
            // repeats the same failure and can starve the rest of the deployment.
            // BranchOverlayIndexingException is raised only for overlay failures that are
            // deterministic for the requested ref and base commit, so it is not replayed either;
            // transient overlay failures arrive unwrapped and follow the normal retry policy.
            // A repository index that failed from indexing onward is not replayed either: the run
            // recorded the failure against the commit (sync_state), and the scheduled sync with
            // its per-commit failure limit decides whether it runs again. Only the cheap pre-index
            // phases (checkout, HEAD check) are retried here.
            var isRepositoryRun = lease.Run.Operation == IndexerRunOperations.ProcessRepository;
            var maxAttempts = ex is RustSemanticIndexingException or BranchOverlayIndexingException
                              || (isRepositoryRun && !IndexingFailure.FailedBeforeIndexing(ex))
                ? 1
                : options.MaxAttempts;
            var disposition = await store.FailOrRetryIndexerRunAsync(
                lease,
                errorCode,
                isRepositoryRun ? IndexingFailure.Describe(ex) : ex.Message,
                UtcNow,
                UtcNow + options.RetryDelay,
                maxAttempts,
                CancellationToken.None);
            logger.Log(
                isRepositoryRun ? LogLevel.Error : LogLevel.Warning,
                ex,
                "Indexer run {RunId} ({Operation} {Target}) failed in phase {Phase} with durable disposition {Disposition}",
                lease.Run.Id,
                lease.Run.Operation,
                lease.Run.Target,
                IndexingFailure.GetPhase(ex),
                disposition);
        }

        return true;
    }

    /// <summary>
    /// Summarizes non-fatal degradations recorded while the run executed. A Rust semantic
    /// fallback completes the run but records its failure code and affected crates.
    /// One fallback is recorded per failed crate group. The error code is
    /// <c>rust_semantic_fallback_&lt;category&gt;</c> when every fallback shares one
    /// <see cref="RustSemanticFailureCategory"/>, and <c>rust_semantic_fallback_mixed</c>
    /// otherwise, independent of group order. The detail is a JSON array with one entry per
    /// fallback (manifest, crates, failureCode, detailCode, message) in the workspace's
    /// deterministic group order, so each group's own code stays visible under "mixed".
    /// </summary>
    private static (string? Code, string? Detail, string? Summary) DescribeDegradations(
        IndexingRunDiagnostics? diagnostics)
    {
        var fallbacks = diagnostics?.RustSemanticFallbacks ?? [];
        if (fallbacks.Count == 0)
            return (null, null, null);

        var failureCodes = fallbacks.Select(fallback => fallback.FailureCode).Distinct().ToList();
        var code = "rust_semantic_fallback_" + (failureCodes.Count == 1 ? failureCodes[0] : "mixed");
        var detail = SerializeDegradationDetail(fallbacks);
        var summary = "Rust semantic indexing fell back to low-confidence tree-sitter extraction for " +
                      string.Join("; ", fallbacks.Select(fallback =>
                          $"{fallback.Manifest} [{fallback.FailureCode}] crates: " +
                          (fallback.Crates.Count == 0 ? "(unknown)" : string.Join(", ", fallback.Crates)))) +
                      ".";
        return (code, detail, summary);
    }

    private static readonly JsonSerializerOptions DegradationJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Per-fallback cap on the analyzer message (stderr tails can reach 64 KB).</summary>
    internal const int MaxFallbackMessageChars = 4096;

    /// <summary>indexer_runs.error is a TEXT column: at most 65535 bytes.</summary>
    internal const int MaxDegradationDetailBytes = 65535;

    /// <summary>
    /// Serializes fallbacks as a JSON array that fits indexer_runs.error. Messages are
    /// truncated first; if the record still does not fit, messages and then crate lists are
    /// dropped so the detail stays valid JSON and keeps each failure code and manifest.
    /// </summary>
    internal static string SerializeDegradationDetail(IReadOnlyList<RustSemanticFallback> fallbacks)
    {
        var detail = JsonSerializer.Serialize(
            fallbacks.Select(fallback => fallback with { Message = Truncate(fallback.Message) }),
            DegradationJsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(detail) <= MaxDegradationDetailBytes)
            return detail;

        detail = JsonSerializer.Serialize(
            fallbacks.Select(fallback => fallback with { Message = "" }),
            DegradationJsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(detail) <= MaxDegradationDetailBytes)
            return detail;

        return JsonSerializer.Serialize(
            fallbacks.Select(fallback => fallback with { Crates = [], Message = "" }),
            DegradationJsonOptions);
    }

    private static string Truncate(string message) =>
        message.Length <= MaxFallbackMessageChars
            ? message
            : message[..MaxFallbackMessageChars] + "...[truncated]";

    private async Task<LeaseMonitorResult> MonitorLeaseAsync(
        IndexerRunLease lease,
        IndexerRunWorkerOptions options,
        CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(options.HeartbeatInterval, _time, ct);
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IIndexerRunStore>();
            var now = UtcNow;
            IndexerRunLeaseRenewal renewal;
            try
            {
                renewal = await store.RenewIndexerRunLeaseAsync(
                    lease.Run.Id,
                    lease.Owner,
                    lease.FencingToken,
                    now,
                    now + options.LeaseDuration,
                    ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One failed heartbeat (a database blip) must not abort an hours-long run. The next
                // heartbeat tries again; once the lease has expired, renewal reports it lost.
                logger.LogWarning(ex, "Indexer run {RunId} heartbeat failed; retrying", lease.Run.Id);
                continue;
            }
            if (!renewal.Renewed)
                return LeaseMonitorResult.LeaseLost;
            if (renewal.CancellationRequested)
                return LeaseMonitorResult.CancellationRequested;
        }
    }

    private static async Task IgnoreMonitorCancellationAsync(Task monitor)
    {
        try
        {
            await monitor;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ValidateOptions(IndexerRunWorkerOptions options)
    {
        if (options.HeartbeatInterval <= TimeSpan.Zero || options.LeaseDuration <= options.HeartbeatInterval)
            throw new InvalidOperationException("Indexer run lease duration must be greater than its positive heartbeat interval.");
        if (options.PollInterval <= TimeSpan.Zero || options.RetryDelay < TimeSpan.Zero || options.MaxAttempts < 1)
            throw new InvalidOperationException("Indexer run polling and retry settings are invalid.");
    }

    private enum LeaseMonitorResult
    {
        LeaseLost,
        CancellationRequested
    }
}
