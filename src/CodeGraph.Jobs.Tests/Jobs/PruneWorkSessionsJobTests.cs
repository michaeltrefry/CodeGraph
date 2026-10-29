using CodeGraph.Models.Work;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Jobs.Tests.Jobs;

public class PruneWorkSessionsJobTests
{
    [Fact]
    public async Task Dispatcher_runs_the_prune_job_with_a_30_day_cutoff_by_default()
    {
        var pruner = new RecordingWorkSessionPruner();
        var dispatcher = JobCommandDispatcherTests.CreateDispatcherWith(pruner, new WorkSessionOptions());

        var before = DateTime.UtcNow;
        var result = await dispatcher.ExecuteAsync(JobTypes.PruneWorkSessions, "{}");

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("3 sessions");
        var call = pruner.Calls.ShouldHaveSingleItem();
        call.Cutoff.ShouldBeInRange(before.AddDays(-30).AddSeconds(-1), DateTime.UtcNow.AddDays(-30));
        call.BatchSize.ShouldBe(new WorkSessionOptions().PruneBatchSize);
    }

    [Fact]
    public async Task The_retention_window_is_configurable()
    {
        var pruner = new RecordingWorkSessionPruner();
        var dispatcher = JobCommandDispatcherTests.CreateDispatcherWith(pruner, new WorkSessionOptions { RetentionDays = 7, PruneBatchSize = 10 });

        await dispatcher.ExecuteAsync(JobTypes.PruneWorkSessions, "{}");

        var call = pruner.Calls.ShouldHaveSingleItem();
        call.Cutoff.ShouldBeInRange(DateTime.UtcNow.AddDays(-7).AddSeconds(-5), DateTime.UtcNow.AddDays(-7));
        call.BatchSize.ShouldBe(10);
    }

    [Fact]
    public void The_job_type_is_registered_supported_and_normalizes_empty_arguments()
    {
        var dispatcher = JobCommandDispatcherTests.CreateDispatcherWith(new RecordingWorkSessionPruner(), new WorkSessionOptions());

        dispatcher.GetSupportedJobTypes().ShouldContain(JobTypes.PruneWorkSessions);
        dispatcher.NormalizeArgsJson(JobTypes.PruneWorkSessions, null).ShouldBe("{}");
    }
}
