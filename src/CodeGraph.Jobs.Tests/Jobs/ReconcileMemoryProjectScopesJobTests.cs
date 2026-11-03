using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Models.Memory;

namespace CodeGraph.Jobs.Tests.Jobs;

public class ReconcileMemoryProjectScopesJobTests
{
    private static readonly ProjectRepositories Alpha = new("ALPHA", ["alpha-api", "alpha-web"]);
    private static readonly ProjectRepositories Beta = new("BETA", ["beta"]);
    private static readonly MemoryScopeWatermark Mark = new(10, 20, 30);

    [Fact]
    public async Task A_run_after_no_change_scans_nothing()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill { Memory = Mark };
        var state = new InMemoryReconciliationStore { Watermark = Mark };

        var result = await CreateJob(backfill, state, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        result.Success.ShouldBeTrue(result.Message);
        backfill.Scans.ShouldBe(0);
        state.Watermark.ShouldBe(Mark);
        result.Message.ShouldContain("no new memory");
    }

    [Fact]
    public async Task The_first_run_records_the_watermark_without_a_scan()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill { Memory = Mark };
        var state = new InMemoryReconciliationStore();

        var result = await CreateJob(backfill, state, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        result.Success.ShouldBeTrue(result.Message);
        backfill.Scans.ShouldBe(0);
        state.Watermark.ShouldBe(Mark);
    }

    [Fact]
    public async Task Only_pending_projects_get_a_full_backfill_of_their_pending_repositories_still_members()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill { Memory = Mark };
        // ALPHA added alpha-web, then dropped alpha-old; GONE was deleted.
        var state = new InMemoryReconciliationStore { Watermark = Mark, Members = MembersOf(Alpha, Beta) }
            .WithPending("ALPHA", "alpha-web", "alpha-old")
            .WithPending("GONE", "gone");

        var result = await CreateJob(backfill, state, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        result.Success.ShouldBeTrue(result.Message);
        backfill.Calls.ShouldHaveSingleItem().ProjectKey.ShouldBe("ALPHA");
        backfill.Calls[0].Repositories.ShouldBe(["alpha-web"]);
        backfill.RangeCalls.ShouldBeEmpty();
        state.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pending_project_or_repository_missing_from_the_listing_is_not_backfilled_and_stays_pending()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill { Memory = Mark };
        // NEW was created, and ALPHA gained alpha-new, after the listing the run works from.
        var state = new InMemoryReconciliationStore
            {
                Watermark = Mark,
                Members = MembersOf(new ProjectRepositories("ALPHA", ["alpha-api", "alpha-web", "alpha-new"]), Beta, new ProjectRepositories("NEW", ["new"])),
            }
            .WithPending("NEW", "new")
            .WithPending("ALPHA", "alpha-api", "alpha-new");

        var result = await CreateJob(backfill, state, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        result.Success.ShouldBeTrue(result.Message);
        var call = backfill.Calls.ShouldHaveSingleItem();
        call.ProjectKey.ShouldBe("ALPHA");
        call.Repositories.ShouldBe(["alpha-api"]);
        state.Pending["NEW"].ShouldBe(["new"]);
        state.Pending["ALPHA"].ShouldBe(["alpha-new"]);
    }

    [Fact]
    public async Task A_large_range_is_scanned_in_spans_and_a_failed_span_keeps_the_spans_before_it()
    {
        var newer = new MemoryScopeWatermark(35, 22, 30);
        var backfill = new RecordingMemoryProjectScopeBackfill { Memory = newer, FailRangeCall = 3 };
        var state = new InMemoryReconciliationStore { Watermark = Mark };

        var result = await CreateJob(backfill, state, rangeSpan: 10, Alpha).ExecuteAsync(new EmptyJobRequest());

        result.Success.ShouldBeFalse();
        backfill.RangeCalls.Select(call => (call.After, call.UpTo)).ShouldBe(
        [
            (Mark, new MemoryScopeWatermark(20, 22, 30)),
            (new MemoryScopeWatermark(20, 22, 30), new MemoryScopeWatermark(30, 22, 30)),
            (new MemoryScopeWatermark(30, 22, 30), newer),
        ]);
        state.Watermark.ShouldBe(new MemoryScopeWatermark(30, 22, 30));

        // The next run resumes from the last completed span.
        backfill.FailRangeCall = null;
        (await CreateJob(backfill, state, rangeSpan: 10, Alpha).ExecuteAsync(new EmptyJobRequest())).Success.ShouldBeTrue();
        backfill.RangeCalls.Count.ShouldBe(4);
        backfill.RangeCalls[3].After.ShouldBe(new MemoryScopeWatermark(30, 22, 30));
        backfill.RangeCalls[3].UpTo.ShouldBe(newer);
        state.Watermark.ShouldBe(newer);
    }

    [Fact]
    public async Task A_watermark_ahead_of_memory_starts_again_as_a_first_run_with_every_project_pending()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill { Memory = Mark };
        var state = new InMemoryReconciliationStore { Watermark = new MemoryScopeWatermark(10, 500, 30) };

        var result = await CreateJob(backfill, state, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("reset");
        backfill.Calls.Select(call => $"{call.ProjectKey}:{string.Join(",", call.Repositories)}").ShouldBe(["ALPHA:alpha-api,alpha-web", "BETA:beta"]);
        backfill.RangeCalls.ShouldBeEmpty();
        state.Watermark.ShouldBe(Mark);
        state.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task New_memory_is_backfilled_for_every_project_in_its_range_only_and_advances_the_watermark()
    {
        var newer = new MemoryScopeWatermark(12, 20, 35);
        var backfill = new RecordingMemoryProjectScopeBackfill { Memory = newer };
        var state = new InMemoryReconciliationStore { Watermark = Mark };

        var result = await CreateJob(backfill, state, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        result.Success.ShouldBeTrue(result.Message);
        backfill.Calls.ShouldBeEmpty();
        var range = backfill.RangeCalls.ShouldHaveSingleItem();
        range.Projects.Select(project => project.ProjectKey).ShouldBe(["ALPHA", "BETA"]);
        range.After.ShouldBe(Mark);
        range.UpTo.ShouldBe(newer);
        state.Watermark.ShouldBe(newer);
    }

    [Fact]
    public async Task A_failed_project_does_not_stop_the_others_fails_the_run_naming_it_and_stays_pending()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill("ALPHA") { Memory = Mark };
        var state = new InMemoryReconciliationStore { Watermark = Mark }
            .WithPending("ALPHA", "alpha-api")
            .WithPending("BETA", "beta");

        var result = await CreateJob(backfill, state, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        backfill.Calls.Select(call => call.ProjectKey).ShouldBe(["ALPHA", "BETA"]);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("ALPHA");
        result.Message.ShouldContain("memory host unreachable");
        result.Message.ShouldNotContain("BETA (");
        state.Pending.Keys.ShouldBe(["ALPHA"]);
        state.Failures["ALPHA"].ShouldBe((1, "memory host unreachable"));

        // The next run repairs it.
        var repaired = new RecordingMemoryProjectScopeBackfill { Memory = Mark };
        (await CreateJob(repaired, state, Alpha, Beta).ExecuteAsync(new EmptyJobRequest())).Success.ShouldBeTrue();
        repaired.Calls.ShouldHaveSingleItem().ProjectKey.ShouldBe("ALPHA");
        state.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_range_backfill_keeps_the_watermark_so_the_next_run_retries_it()
    {
        var newer = new MemoryScopeWatermark(11, 21, 31);
        var backfill = new RecordingMemoryProjectScopeBackfill { Memory = newer, FailRange = true };
        var state = new InMemoryReconciliationStore { Watermark = Mark };

        var result = await CreateJob(backfill, state, Alpha).ExecuteAsync(new EmptyJobRequest());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("new memory");
        state.Watermark.ShouldBe(Mark);

        backfill.FailRange = false;
        (await CreateJob(backfill, state, Alpha).ExecuteAsync(new EmptyJobRequest())).Success.ShouldBeTrue();
        backfill.RangeCalls[1].After.ShouldBe(Mark);
        state.Watermark.ShouldBe(newer);
    }

    [Fact]
    public async Task The_dispatcher_runs_the_reconciliation_with_empty_args()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill();
        var dispatcher = new JobCommandDispatcher(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            CreateJob(backfill, new InMemoryReconciliationStore().WithPending("BETA", "beta"), Beta), null!);

        dispatcher.GetSupportedJobTypes().ShouldContain(JobTypes.ReconcileMemoryProjectScopes);
        dispatcher.NormalizeArgsJson(JobTypes.ReconcileMemoryProjectScopes, null).ShouldBe("{}");
        (await dispatcher.ExecuteAsync(JobTypes.ReconcileMemoryProjectScopes, "{}")).Success.ShouldBeTrue();
        backfill.Calls.Single().ProjectKey.ShouldBe("BETA");
    }

    private static ReconcileMemoryProjectScopesJob CreateJob(RecordingMemoryProjectScopeBackfill backfill, InMemoryReconciliationStore state, params ProjectRepositories[] projects) =>
        CreateJob(backfill, state, ReconcileMemoryProjectScopesJob.DefaultRangeSpan, projects);

    private static ReconcileMemoryProjectScopesJob CreateJob(RecordingMemoryProjectScopeBackfill backfill, InMemoryReconciliationStore state, long rangeSpan, params ProjectRepositories[] projects) =>
        new(ProjectRepositoriesWorkStore.Create(projects), state, backfill, NullLogger<ReconcileMemoryProjectScopesJob>.Instance, rangeSpan);

    private static Dictionary<string, string[]> MembersOf(params ProjectRepositories[] projects) =>
        projects.ToDictionary(project => project.ProjectKey, project => project.Repositories.ToArray());
}
