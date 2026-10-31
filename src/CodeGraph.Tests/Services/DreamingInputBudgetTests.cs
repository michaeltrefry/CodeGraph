using System.Reflection;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class DreamingInputBudgetTests
{
    [Fact]
    public async Task Admission_loop_can_progress_past_large_utf8_replay_to_a_write_action_when_count_fits()
    {
        var fixture = new Fixture();
        var run = await fixture.RunAsync();

        run.Status.ShouldBe("completed", run.Reason);
        fixture.Model.Requests.Count.ShouldBe(3);
        fixture.SourceReads.ShouldBe(1);
        fixture.Writes.ShouldBe(1);
        run.ActionCounts["dream_record"].ShouldBe(1);
        (run.InputTokens + run.OutputTokens).ShouldBe(67220);
        fixture.Store.Saved!.ShouldBe(run);
        fixture.Model.Requests.ShouldAllBe(request => request.MaxTokens == 8192);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Unavailable_or_invalid_count_falls_back_only_when_bytes_independently_fit(long? count)
    {
        var small = new Fixture { LargeReplay = false };
        small.Model.Count = _ => count;
        (await small.RunAsync()).Status.ShouldBe("completed");
        small.Writes.ShouldBe(1);
        var large = new Fixture();
        large.Model.Count = _ => count;
        var stopped = await large.RunAsync();
        stopped.Reason.ShouldBe("input_count_uncertain");
        large.Model.Requests.Count.ShouldBe(1);
        large.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task A_count_that_overflows_framing_stops_before_generation()
    {
        var fixture = new Fixture();
        fixture.Model.Count = _ => long.MaxValue;
        (await fixture.RunAsync()).Reason.ShouldBe("input_budget");
        fixture.Model.Requests.ShouldBeEmpty();
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Allocated_output_respects_remaining_after_the_existing_framing_margin()
    {
        var fixture = new Fixture();
        fixture.Store.Settings = new DreamSettings(MaxTokensPerRun: 8000);
        fixture.Model.Count = _ => 1000;
        var run = await fixture.RunAsync();
        fixture.Model.Requests.Single().MaxTokens.ShouldBe(984);
        run.Reason.ShouldBe("input_budget");
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Unsupported_preparation_keeps_the_original_byte_guard()
    {
        var fixture = new Fixture();
        fixture.Model.UsePrepared = false;
        (await fixture.RunAsync()).Reason.ShouldBe("input_budget");
        fixture.Model.CountCalls.ShouldBe(0);
        fixture.Model.Requests.Count.ShouldBe(1);
        fixture.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(127950L, 50L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public async Task Actual_usage_exhaustion_or_overflow_stops_before_any_tool_action(long input, long output)
    {
        var fixture = new Fixture();
        fixture.Model.Reply = _ => new(null, [new("write", "dream_record", JsonSerializer.SerializeToElement(new { }))], input, output);
        var run = await fixture.RunAsync();
        run.Reason.ShouldBe("token_budget");
        run.InputTokens.ShouldBe(input);
        run.OutputTokens.ShouldBe(output);
        run.Actions.ShouldBeEmpty();
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Count_preflight_does_not_extend_the_configured_turn_limit()
    {
        var fixture = new Fixture();
        fixture.Store.Settings = new DreamSettings(MaxTurns: 1);
        (await fixture.RunAsync()).Reason.ShouldBe("turn_budget");
        fixture.Model.Requests.Count.ShouldBe(1);
        fixture.Model.CountCalls.ShouldBe(1);
        fixture.Writes.ShouldBe(0);
    }

    private sealed class Fixture : IKnowledgeDreamActions, IDreamSourceReader
    {
        public Guid Project { get; } = Guid.NewGuid();
        public RunStore Store { get; } = new();
        public ScriptedModel Model { get; } = new();
        public int SourceReads { get; private set; }
        public int Writes { get; private set; }
        public bool LargeReplay { get; set; } = true;
        public Task<DreamRun> RunAsync()
        {
            var work = DispatchProxy.Create<IWorkStore, ProjectProxy>();
            ((ProjectProxy)(object)work).Project = Project;
            return new DreamingService(Store, work, this, Model, this).RunAsync(Project, "schedule", new("owner"));
        }
        public Task<DreamDigest> BuildDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default) =>
            Task.FromResult(new DreamDigest(JsonSerializer.SerializeToElement(new { changed = true }), "new-signal", true));
        public Task<object> ExecuteAsync(Guid projectId, string tool, JsonElement args, CancellationToken ct = default)
        {
            tool.ShouldBe("dream_read_source");
            SourceReads++;
            return Task.FromResult<object>(new { source = LargeReplay ? string.Concat(Enumerable.Repeat("let café = \"界\"; // evidence\n", 5000)) : "small source" });
        }
        public Task<object> ExecuteToolAsync(string tool, JsonElement args, WorkActor actor, CancellationToken ct = default)
        {
            tool.ShouldBe("dream_record");
            SourceReads.ShouldBe(1);
            actor.Kind.ShouldBe(WorkActor.SystemKind);
            Writes++;
            return Task.FromResult<object>(new { entry = new { id = Guid.NewGuid(), summary = "Source-backed observation" } });
        }
        public Task<DreamRevertResult> RevertAsync(Guid projectId, Guid runId, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ScriptedModel : IDreamBatchModel
    {
        public List<DreamModelRequest> Requests { get; } = [];
        public bool UsePrepared { get; set; } = true;
        public Func<int, long?> Count { get; set; } = call => call == 1 ? 7000 : 30000;
        public Func<int, DreamModelResponse>? Reply { get; set; }
        public int CountCalls { get; private set; }
        public bool SupportsTools(string provider) => true;
        public Task<IDreamPreparedTurn?> PrepareAsync(DreamModelRequest request, CancellationToken ct = default) => Task.FromResult<IDreamPreparedTurn?>(UsePrepared ? new Turn(this, request) : null);
        public Task<DreamModelResponse> CompleteAsync(DreamModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Reply is not null) return Task.FromResult(Reply(Requests.Count));
            var call = Requests.Count switch
            {
                1 => new DreamToolCall("read", "dream_read_source", JsonSerializer.SerializeToElement(new { repository = "codegraph", path = "Source.cs" })),
                2 => new DreamToolCall("write", "dream_record", JsonSerializer.SerializeToElement(new { entry = new { kind = "fact", summary = "Source-backed observation", confidence = "high" }, evidence = new[] { new { kind = "entry", reference = "d915b0cc-f63c-4fb6-9fca-6de2af91f062", summary = "Checked source" } }, prevalence = "one file" })),
                _ => null
            };
            return Task.FromResult(new DreamModelResponse("Report", call is null ? [] : [call], Requests.Count == 1 ? 7000 : 30000, Requests.Count < 3 ? 100 : 20));
        }
        private sealed class Turn(ScriptedModel owner, DreamModelRequest request) : IDreamPreparedTurn
        {
            public Task<long?> CountInputTokensAsync(CancellationToken ct = default) => Task.FromResult(owner.Count(++owner.CountCalls));
            public Task<DreamModelResponse> CompleteAsync(int maxTokens, CancellationToken ct = default) => owner.CompleteAsync(request with { MaxTokens = maxTokens }, ct);
        }
    }

    public class ProjectProxy : DispatchProxy
    {
        public Guid Project { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == nameof(IWorkStore.GetProjectAsync)
            ? Task.FromResult<ProjectView?>(new(Project, "TEST", "Test", null, [], [], 1, "owner", DateTime.UtcNow, DateTime.UtcNow))
            : throw new NotSupportedException(method.Name);
    }

    private sealed class RunStore : IDreamingStore
    {
        public DreamRun? Saved { get; private set; }
        public DreamCheckpoint? Checkpoint { get; private set; }
        public Task<DreamCheckpoint?> GetCheckpointAsync(Guid projectId,CancellationToken ct=default)=>Task.FromResult(Checkpoint?.ProjectId==projectId?Checkpoint:null);
        public Task<DreamExecutionOutcome?> GetExecutionResultAsync(Guid projectId,Guid executionId,CancellationToken ct=default)=>Task.FromResult<DreamExecutionOutcome?>(null);
        public Task SaveProgressAsync(DreamRun run,JsonElement? checkpoint,Guid? expectedCheckpointRunId,CancellationToken ct=default)
        {if(Checkpoint?.RunId!=expectedCheckpointRunId)throw new InvalidOperationException("checkpoint conflict");Saved=run;Checkpoint=checkpoint is {} state?new(run.ProjectId,run.Id,state.Clone()):null;return Task.CompletedTask;}

        public DreamSettings Settings { get; set; } = new(MinimumIntervalMinutes: 0);
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid projectId, CancellationToken ct = default) => Task.FromResult<IAsyncDisposable?>(new Lease());
        public Task SaveRunAsync(DreamRun run, CancellationToken ct = default) { Saved = run; return Task.CompletedTask; }
        public Task<DreamRun?> LastCompletedAsync(Guid projectId, CancellationToken ct = default) => Task.FromResult<DreamRun?>(null);
        public Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);
        public Task<DreamInstructionSet> GetInstructionsAsync(Guid projectId, CancellationToken ct = default) => Task.FromResult(new DreamInstructionSet(new("base", "Curate source-backed knowledge", 1, []), new("project", "", 1, [])));
        public Task<IReadOnlyList<Guid>> ListProjectIdsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamRun?> GetRunAsync(Guid projectId, Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DreamRun>> ListRunsBeforeAsync(Guid projectId, Guid? before, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamSettings> SetSettingsAsync(DreamSettings settings, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DreamInstructions> SetInstructionsAsync(Guid projectId, string scope, string content, int expectedRevision, WorkActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
