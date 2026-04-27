using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Assistant;

public sealed class AssistantRunBackgroundRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<AssistantRunBackgroundRunner> logger) : IAssistantRunBackgroundRunner
{
    private readonly object runnerLock = new();
    private readonly Dictionary<long, (Task Task, CancellationTokenSource Cancellation)> runners = new();

    public Task EnqueueAsync(long runId, CancellationToken ct = default)
    {
        lock (runnerLock)
        {
            if (runners.TryGetValue(runId, out var existing) && !existing.Task.IsCompleted)
                return Task.CompletedTask;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var runner = Task.Run(() => RunInBackgroundAsync(runId, cts.Token), CancellationToken.None);
            runners[runId] = (runner, cts);

            _ = runner.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    logger.LogError(t.Exception, "Assistant run background runner crashed for run {RunId}", runId);

                lock (runnerLock)
                {
                    if (runners.TryGetValue(runId, out var current) && ReferenceEquals(current.Task, runner))
                    {
                        current.Cancellation.Dispose();
                        runners.Remove(runId);
                    }
                }
            }, TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    public Task<bool> CancelAsync(long runId, CancellationToken ct = default)
    {
        lock (runnerLock)
        {
            if (!runners.TryGetValue(runId, out var runner) || runner.Task.IsCompleted)
                return Task.FromResult(false);

            runner.Cancellation.Cancel();
            return Task.FromResult(true);
        }
    }

    private async Task RunInBackgroundAsync(long runId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAssistantRunService>();
        await service.ExecuteRunAsync(runId, ct);
    }
}
