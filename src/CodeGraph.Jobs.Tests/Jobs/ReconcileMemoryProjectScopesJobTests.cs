using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Data;
using CodeGraph.Jobs.Jobs;

namespace CodeGraph.Jobs.Tests.Jobs;

public class ReconcileMemoryProjectScopesJobTests
{
    private static readonly ProjectRepositories Alpha = new("ALPHA", ["alpha-api", "alpha-web"]);
    private static readonly ProjectRepositories Beta = new("BETA", ["beta"]);

    [Fact]
    public async Task A_failed_project_does_not_stop_the_others_and_fails_the_run_naming_it()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill("ALPHA");
        var result = await CreateJob(backfill, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        backfill.Calls.Select(call => call.ProjectKey).ShouldBe(["ALPHA", "BETA"]);
        backfill.Calls[1].Repositories.ShouldBe(["beta"]);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("ALPHA");
        result.Message.ShouldContain("memory host unreachable");
        result.Message.ShouldNotContain("BETA");
    }

    [Fact]
    public async Task Every_project_backfilled_succeeds_with_the_count()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill();
        var result = await CreateJob(backfill, Alpha, Beta).ExecuteAsync(new EmptyJobRequest());

        backfill.Calls.Count.ShouldBe(2);
        backfill.Calls[0].Repositories.ShouldBe(["alpha-api", "alpha-web"]);
        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 projects");
    }

    [Fact]
    public async Task The_dispatcher_runs_the_reconciliation_with_empty_args()
    {
        var backfill = new RecordingMemoryProjectScopeBackfill();
        var dispatcher = new JobCommandDispatcher(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            CreateJob(backfill, Beta), null!);

        dispatcher.GetSupportedJobTypes().ShouldContain(JobTypes.ReconcileMemoryProjectScopes);
        dispatcher.NormalizeArgsJson(JobTypes.ReconcileMemoryProjectScopes, null).ShouldBe("{}");
        (await dispatcher.ExecuteAsync(JobTypes.ReconcileMemoryProjectScopes, "{}")).Success.ShouldBeTrue();
        backfill.Calls.Single().ProjectKey.ShouldBe("BETA");
    }

    private static ReconcileMemoryProjectScopesJob CreateJob(RecordingMemoryProjectScopeBackfill backfill, params ProjectRepositories[] projects) =>
        new(ProjectRepositoriesWorkStore.Create(projects), backfill, NullLogger<ReconcileMemoryProjectScopesJob>.Instance);
}
