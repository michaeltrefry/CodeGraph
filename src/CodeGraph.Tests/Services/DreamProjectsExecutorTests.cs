using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class DreamProjectsExecutorTests
{
    [Fact]
    public async Task VisitsEveryProjectOnIndexerAndPreservesScheduledIdentity()
    {
        var projects = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var service = new RecordingDreamingService();
        var result = await Executor(projects, service).ExecuteAsync(Lease());
        service.Calls.Select(c => c.Project).ShouldBe(projects);
        service.Calls.ShouldAllBe(c => c.Trigger == "schedule" && c.Actor.Name == "codegraph-jobs" && c.Actor.Kind == WorkActor.SystemKind);
        result.ShouldContain("2 projects");
    }

    [Fact]
    public async Task FailingProjectDoesNotPreventOtherProjectsFromRunning()
    {
        var projects = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var service = new RecordingDreamingService { FailProject = projects[0] };
        var failure = await Should.ThrowAsync<AggregateException>(() => Executor(projects, service).ExecuteAsync(Lease()));
        service.Calls.Select(c => c.Project).ShouldBe(projects);
        failure.InnerExceptions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CancellationStopsSweep()
    {
        var service = new RecordingDreamingService();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Executor([Guid.NewGuid()], service).ExecuteAsync(Lease(), source.Token));
        service.Calls.ShouldBeEmpty();
    }

    private static IndexerRunLease Lease() => new(new IndexerRunEntity
    { Id = 1, Operation = IndexerRunOperations.DreamProjects, RequestedByUsername = "codegraph-jobs" }, "worker", 1);

    // Other execution paths' collaborators are intentionally absent: the dreaming operation must not use them.
    private static IndexerRunExecutor Executor(IReadOnlyList<Guid> projects, IDreamingService service)
        => new(null!, null!, null!, null!, dreamingStore: new ProjectStore(projects), dreamingService: service);

    private sealed class RecordingDreamingService : IDreamingService
    {
        public List<(Guid Project, string Trigger, WorkActor Actor)> Calls { get; } = [];
        public Guid? FailProject { get; init; }
        public Task<DreamRun> RunAsync(Guid projectId, string trigger, WorkActor initiator, CancellationToken ct = default)
        {
            Calls.Add((projectId, trigger, initiator));
            if (projectId == FailProject) throw new InvalidOperationException("project failure");
            return Task.FromResult<DreamRun>(null!);
        }
        public Task<DreamRun?> GetRunAsync(Guid projectId, Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSettingsAsync(DreamSettings settings, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructionSet> GetInstructionsAsync(Guid projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructions> SetInstructionsAsync(Guid projectId, string scope, string content, int expectedRevision, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ProjectStore(IReadOnlyList<Guid> projects) : IDreamingStore
    {
        public Task<IReadOnlyList<Guid>> ListProjectIdsAsync(CancellationToken ct = default) => Task.FromResult(projects);
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveRunAsync(DreamRun run, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamRun?> GetRunAsync(Guid projectId, Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamRun?> LastCompletedAsync(Guid projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSettingsAsync(DreamSettings settings, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructionSet> GetInstructionsAsync(Guid projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructions> SetInstructionsAsync(Guid projectId, string scope, string content, int expectedRevision, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
