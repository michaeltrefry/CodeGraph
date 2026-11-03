using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Memory;

/// <summary>
/// Hands a project's memory scope backfill (CG-167) to a background worker, off the request path.
/// The backfill scans every user's memory, so a burst of project creates or repository changes must
/// not run one scan per request: pending work is coalesced per project and run one project at a time.
/// </summary>
public interface IMemoryScopeBackfillQueue
{
    /// <summary>
    /// Queues a backfill of the given repositories for the project, or unions them into the one already
    /// pending for it. False when the queue is full: the daily ReconcileMemoryProjectScopes job covers it.
    /// </summary>
    bool Enqueue(string projectKey, IReadOnlyCollection<string> repositories);
}

/// <summary>
/// The pending backfills, at most one per project, in the order projects were first queued. A
/// project taken by the worker leaves the queue, so one queued again while its backfill runs is run
/// again afterwards with the repositories added meanwhile.
/// </summary>
public sealed class MemoryScopeBackfillQueue(ILogger<MemoryScopeBackfillQueue> logger, int capacity = MemoryScopeBackfillQueue.DefaultCapacity)
    : IMemoryScopeBackfillQueue
{
    /// <summary>The most projects pending at once.</summary>
    public const int DefaultCapacity = 1000;

    private readonly object gate = new();
    private readonly Dictionary<string, HashSet<string>> pending = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private readonly SemaphoreSlim available = new(0);

    public bool Enqueue(string projectKey, IReadOnlyCollection<string> repositories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        ArgumentNullException.ThrowIfNull(repositories);
        if (repositories.Count == 0)
            return true;

        lock (gate)
        {
            if (pending.TryGetValue(projectKey, out var queued))
            {
                queued.UnionWith(repositories);
                return true;
            }
            if (pending.Count >= capacity)
            {
                logger.LogWarning("The memory scope backfill queue is full ({Capacity} projects); project {Project} is left to the daily reconciliation.",
                    capacity, projectKey);
                return false;
            }
            pending[projectKey] = new HashSet<string>(repositories, StringComparer.OrdinalIgnoreCase);
            order.Enqueue(projectKey);
        }
        available.Release();
        return true;
    }

    /// <summary>Waits for the next pending project and takes it, with its repositories, off the queue.</summary>
    public async Task<(string ProjectKey, IReadOnlyCollection<string> Repositories)> DequeueAsync(CancellationToken ct)
    {
        await available.WaitAsync(ct);
        lock (gate)
        {
            var projectKey = order.Dequeue();
            pending.Remove(projectKey, out var repositories);
            return (projectKey, repositories!.ToList());
        }
    }
}

/// <summary>
/// Runs the queued memory scope backfills one project at a time, each in its own DI scope. A failed
/// backfill is logged and the worker moves on; the daily ReconcileMemoryProjectScopes job retries it.
/// </summary>
public sealed class MemoryScopeBackfillWorker(
    MemoryScopeBackfillQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<MemoryScopeBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            (string ProjectKey, IReadOnlyCollection<string> Repositories) next;
            try
            {
                next = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            await BackfillAsync(next.ProjectKey, next.Repositories, stoppingToken);
        }
    }

    private async Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IMemoryProjectScopeBackfill>() is not { } backfill)
            {
                logger.LogWarning("No memory scope backfill is configured; project {Project} is not scoped.", projectKey);
                return;
            }
            await backfill.BackfillAsync(projectKey, repositories, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // An HTTP client timeout surfaces as an OperationCanceledException too.
            logger.LogWarning(exception, "Memory naming the repositories of project {Project} could not be scoped to it.", projectKey);
        }
    }
}

public static class MemoryScopeBackfillServiceCollectionExtensions
{
    /// <summary>Registers the memory scope backfill queue and the hosted worker that drains it.</summary>
    public static IServiceCollection AddMemoryScopeBackfillQueue(this IServiceCollection services)
    {
        services.AddSingleton(sp => new MemoryScopeBackfillQueue(sp.GetRequiredService<ILogger<MemoryScopeBackfillQueue>>()));
        services.AddSingleton<IMemoryScopeBackfillQueue>(sp => sp.GetRequiredService<MemoryScopeBackfillQueue>());
        services.AddHostedService<MemoryScopeBackfillWorker>();
        return services;
    }
}
