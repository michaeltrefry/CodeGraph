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
    public async Task ReturnedFailedRunAndThrownFailureBothFailSweepAfterVisitingEveryProject()
    {
        var projects = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var service = new RecordingDreamingService { FailProject = projects[1] };
        service.Statuses[projects[0]] = "failed";
        service.Statuses[projects[2]] = "completed";
        service.Statuses[projects[3]] = "skipped";
        service.Statuses[projects[4]] = "budget_exhausted";

        var failure = await Should.ThrowAsync<AggregateException>(() => Executor(projects, service).ExecuteAsync(Lease()));

        service.Calls.Select(c => c.Project).ShouldBe(projects);
        failure.InnerExceptions.Count.ShouldBe(2);
        failure.InnerExceptions[0].Message.ShouldContain(projects[0].ToString());
        failure.InnerExceptions[0].Message.ShouldContain("provider failure recorded");
        failure.InnerExceptions[1].Message.ShouldContain(projects[1].ToString());
        failure.InnerExceptions[1].InnerException!.Message.ShouldBe("project failure");
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("skipped")]
    [InlineData("budget_exhausted")]
    public async Task SuccessfulTerminalStatusesDoNotFailSweep(string status)
    {
        var project = Guid.NewGuid();
        var service = new RecordingDreamingService();
        service.Statuses[project] = status;

        var result = await Executor([project], service).ExecuteAsync(Lease());

        result.ShouldContain("1 projects");
        service.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task UnexpectedStatusCannotBeReportedAsCompletedSweep()
    {
        var project = Guid.NewGuid();
        var service = new RecordingDreamingService();
        service.Statuses[project] = "running";
        var failure = await Should.ThrowAsync<AggregateException>(() => Executor([project], service).ExecuteAsync(Lease()));
        failure.InnerExceptions.ShouldHaveSingleItem().Message.ShouldContain("unexpected status 'running'");
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
        public Dictionary<Guid, string> Statuses { get; } = [];
        public Task<DreamRun> RunAsync(Guid projectId, string trigger, WorkActor initiator, CancellationToken ct = default)
        {
            Calls.Add((projectId, trigger, initiator));
            if (projectId == FailProject) throw new InvalidOperationException("project failure");
            var status = Statuses.GetValueOrDefault(projectId, "completed");
            return Task.FromResult(new DreamRun(Guid.NewGuid(), projectId, trigger, initiator, status,
                DateTime.UtcNow, DateTime.UtcNow, "scripted", "scripted", 0, 0, 0, 0, 0, null, [],
                Reason: status == "failed" ? "provider failure recorded" : null));
        }
        public Task<DreamRun?> GetRunAsync(Guid projectId, Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DreamRun>> ListRunsBeforeAsync(Guid projectId, Guid? before, int limit = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamSettings> SetSettingsAsync(DreamSettings settings, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructionSet> GetInstructionsAsync(Guid projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructions> SetInstructionsAsync(Guid projectId, string scope, string content, int expectedRevision, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ProjectStore(IReadOnlyList<Guid> projects) : IDreamingStore
    {
        public Task<DreamCheckpoint?> GetCheckpointAsync(Guid projectId,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<DreamExecutionOutcome?> GetExecutionResultAsync(Guid projectId,Guid executionId,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task SaveProgressAsync(DreamRun run,System.Text.Json.JsonElement? checkpoint,Guid? expectedCheckpointRunId,CancellationToken ct=default)=>throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListProjectIdsAsync(CancellationToken ct = default) => Task.FromResult(projects);
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveRunAsync(DreamRun run, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamRun?> GetRunAsync(Guid projectId, Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamRun?> LastCompletedAsync(Guid projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DreamRun>> ListRunsBeforeAsync(Guid projectId, Guid? before, int limit = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamSettings> SetSettingsAsync(DreamSettings settings, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructionSet> GetInstructionsAsync(Guid projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructions> SetInstructionsAsync(Guid projectId, string scope, string content, int expectedRevision, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
