using System.Threading.Channels;
using CodeGraph.Data;
using CodeGraph.Services.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Memory;

/// <summary>
/// The memory scope backfill queue and worker (CG-167): pending backfills are coalesced per project
/// and bounded, run one project at a time off the request path, and a failing backfill never stops
/// the worker.
/// </summary>
public sealed class MemoryScopeBackfillQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Two_enqueues_for_one_project_run_one_backfill_with_the_union_of_their_repositories()
    {
        var queue = NewQueue();
        queue.Enqueue("P", ["alpha", "beta"]).ShouldBeTrue();
        queue.Enqueue("Q", ["xray"]).ShouldBeTrue();
        queue.Enqueue("P", ["BETA", "gamma"]).ShouldBeTrue();
        queue.Enqueue("R", []).ShouldBeTrue();
        var backfill = new ScriptedBackfill();

        await using (await RunningWorker.StartAsync(queue, backfill))
        {
            (await backfill.NextCallAsync()).ShouldBe(("P", "alpha,beta,gamma"));
            (await backfill.NextCallAsync()).ShouldBe(("Q", "xray"));
        }

        backfill.Calls.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void A_full_queue_refuses_a_new_project_but_still_coalesces_into_a_pending_one()
    {
        var queue = NewQueue(capacity: 2);

        queue.Enqueue("A", ["one"]).ShouldBeTrue();
        queue.Enqueue("B", ["two"]).ShouldBeTrue();
        queue.Enqueue("C", ["three"]).ShouldBeFalse();
        queue.Enqueue("A", ["four"]).ShouldBeTrue();
    }

    [Fact]
    public async Task A_failing_or_timed_out_backfill_does_not_stop_the_next_project()
    {
        var queue = NewQueue();
        queue.Enqueue("FAILS", ["one"]);
        // An HTTP client timeout surfaces as a TaskCanceledException that is not shutdown.
        queue.Enqueue("TIMES_OUT", ["two"]);
        queue.Enqueue("WORKS", ["three"]);
        var backfill = new ScriptedBackfill
        {
            Before = (project, _) => project switch
            {
                "FAILS" => Task.FromException(new InvalidOperationException("memory host unreachable")),
                "TIMES_OUT" => Task.FromException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")),
                _ => Task.CompletedTask,
            },
        };

        await using var worker = await RunningWorker.StartAsync(queue, backfill);
        (await backfill.NextCallAsync()).Project.ShouldBe("FAILS");
        (await backfill.NextCallAsync()).Project.ShouldBe("TIMES_OUT");
        (await backfill.NextCallAsync()).Project.ShouldBe("WORKS");

        queue.Enqueue("LATER", ["four"]);
        (await backfill.NextCallAsync()).ShouldBe(("LATER", "four"));
        worker.Worker.ExecuteTask!.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_project_enqueued_while_its_backfill_runs_is_backfilled_again_afterwards()
    {
        var queue = NewQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var backfill = new ScriptedBackfill
        {
            Before = (_, ct) =>
            {
                if (!first)
                    return Task.CompletedTask;
                first = false;
                return release.Task.WaitAsync(ct);
            },
        };
        queue.Enqueue("P", ["alpha"]);

        await using var worker = await RunningWorker.StartAsync(queue, backfill);
        (await backfill.NextCallAsync()).ShouldBe(("P", "alpha"));
        // The first backfill is still running: the project is pending again, with the new repository.
        queue.Enqueue("P", ["beta"]).ShouldBeTrue();
        release.SetResult();

        (await backfill.NextCallAsync()).ShouldBe(("P", "beta"));
    }

    [Fact]
    public async Task Shutdown_cancels_a_running_backfill_and_stops_the_worker()
    {
        var queue = NewQueue();
        var backfill = new ScriptedBackfill { Before = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        queue.Enqueue("P", ["alpha"]);

        var worker = await RunningWorker.StartAsync(queue, backfill);
        await backfill.NextCallAsync();
        await worker.DisposeAsync();

        worker.Worker.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private static MemoryScopeBackfillQueue NewQueue(int capacity = MemoryScopeBackfillQueue.DefaultCapacity) =>
        new(NullLogger<MemoryScopeBackfillQueue>.Instance, capacity);

    private sealed class RunningWorker : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private RunningWorker(ServiceProvider provider, MemoryScopeBackfillWorker worker)
        {
            this.provider = provider;
            Worker = worker;
        }

        public MemoryScopeBackfillWorker Worker { get; }

        public static async Task<RunningWorker> StartAsync(MemoryScopeBackfillQueue queue, IMemoryProjectScopeBackfill backfill)
        {
            var provider = new ServiceCollection().AddScoped(_ => backfill).BuildServiceProvider();
            var worker = new MemoryScopeBackfillWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MemoryScopeBackfillWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);
            return new RunningWorker(provider, worker);
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None).WaitAsync(Wait);
            Worker.Dispose();
            await provider.DisposeAsync();
        }
    }

    private sealed class ScriptedBackfill : IMemoryProjectScopeBackfill
    {
        public Channel<(string Project, string Repositories)> Calls { get; } = Channel.CreateUnbounded<(string, string)>();

        /// <summary>Runs after a call is recorded, as the backfill itself.</summary>
        public Func<string, CancellationToken, Task> Before { get; init; } = (_, _) => Task.CompletedTask;

        public async Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
        {
            Calls.Writer.TryWrite((projectKey, string.Join(",", repositories.Order(StringComparer.OrdinalIgnoreCase))));
            await Before(projectKey, ct);
        }

        public async Task<(string Project, string Repositories)> NextCallAsync() =>
            await Calls.Reader.ReadAsync().AsTask().WaitAsync(Wait);
    }
}
