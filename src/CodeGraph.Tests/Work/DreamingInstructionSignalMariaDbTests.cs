using CodeGraph.Models.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Theory]
    [InlineData("base")]
    [InlineData("project")]
    public async Task Changed_instruction_revisions_run_once_after_completion_then_unchanged_instructions_skip(string scope)
    {
        var project = await ProjectAsync();
        await RecordAsync(project, "Existing knowledge for instruction retuning");
        var model = new ScriptedModel(Response(), Response());
        var service = await ServiceAsync(model);
        var baseline = await service.RunAsync(project, "run_now", Human);
        baseline.Status.ShouldBe("completed", baseline.Reason);
        model.Requests.Count.ShouldBe(1);
        var before = await service.GetInstructionsAsync(project);
        var page = scope == "base" ? before.Base : before.Project;
        var updated = await service.SetInstructionsAsync(project, scope, page.Content + "\nCheck the tuned evidence contract.", page.Revision, Human);
        db.Clock.Advance(TimeSpan.FromSeconds(1));

        var tuned = await service.RunAsync(project, "run_now", Human);

        tuned.Status.ShouldBe("completed", tuned.Reason);
        model.Requests.Count.ShouldBe(2);
        tuned.SignalFingerprint.ShouldBe(baseline.SignalFingerprint, "instruction tuning requires no fabricated knowledge signal");
        tuned.BaseInstructionRevision.ShouldBe(scope == "base" ? updated.Revision : before.Base.Revision);
        tuned.ProjectInstructionRevision.ShouldBe(scope == "project" ? updated.Revision : before.Project.Revision);
        model.Requests[1].System.ShouldBe(scope == "base" ? updated.Content + "\n\n" + before.Project.Content
            : before.Base.Content + "\n\n" + updated.Content);
        var saved = (await service.GetRunAsync(project, tuned.Id))!;
        saved.BaseInstructionRevision.ShouldBe(tuned.BaseInstructionRevision);
        saved.ProjectInstructionRevision.ShouldBe(tuned.ProjectInstructionRevision);
        db.Clock.Advance(TimeSpan.FromSeconds(1));

        var unchanged = await service.RunAsync(project, "run_now", Human);

        unchanged.Status.ShouldBe("skipped");
        unchanged.Reason.ShouldBe("no_signal");
        model.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Another_projects_addendum_is_not_signal_for_the_current_project()
    {
        var project = await ProjectAsync();
        var other = await ProjectAsync("inference");
        await RecordAsync(project, "Knowledge isolated from another project addendum");
        var model = new ScriptedModel(Response());
        var service = await ServiceAsync(model);
        var baseline = await service.RunAsync(project, "run_now", Human);
        baseline.Status.ShouldBe("completed", baseline.Reason);
        var instructions = await service.GetInstructionsAsync(other);
        await service.SetInstructionsAsync(other, "project", "An addendum only for the other project.", instructions.Project.Revision, Human);
        db.Clock.Advance(TimeSpan.FromSeconds(1));

        var run = await service.RunAsync(project, "run_now", Human);

        run.Status.ShouldBe("skipped");
        run.Reason.ShouldBe("no_signal");
        run.BaseInstructionRevision.ShouldBe(baseline.BaseInstructionRevision);
        run.ProjectInstructionRevision.ShouldBe(baseline.ProjectInstructionRevision);
        model.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Instruction_edits_do_not_start_a_first_run_for_an_empty_project()
    {
        var project = await ProjectAsync();
        var model = new ScriptedModel(Response());
        var service = await ServiceAsync(model);
        var first = await service.RunAsync(project, "run_now", Human);
        first.Status.ShouldBe("skipped");
        first.Reason.ShouldBe("no_signal");
        var instructions = await service.GetInstructionsAsync(project);
        await service.SetInstructionsAsync(project, "project", "A new addendum without any project knowledge.", instructions.Project.Revision, Human);

        var next = await service.RunAsync(project, "run_now", Human);

        next.Status.ShouldBe("skipped");
        next.Reason.ShouldBe("no_signal");
        model.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, 0, "disabled")]
    [InlineData(true, 60, "minimum_interval")]
    public async Task Instruction_changes_do_not_bypass_enabled_or_minimum_interval(bool enabled, int minimumInterval, string reason)
    {
        var project = await ProjectAsync();
        await RecordAsync(project, "Existing knowledge before gated instruction retuning");
        var model = new ScriptedModel(Response());
        var service = await ServiceAsync(model);
        (await service.RunAsync(project, "run_now", Human)).Status.ShouldBe("completed");
        var instructions = await service.GetInstructionsAsync(project);
        await service.SetInstructionsAsync(project, "project", "Tuned instructions respect scheduling gates.", instructions.Project.Revision, Human);
        var settings = await service.GetSettingsAsync();
        await service.SetSettingsAsync(settings with { Enabled = enabled, MinimumIntervalMinutes = minimumInterval }, Human);

        var gated = await service.RunAsync(project, "run_now", Human);

        gated.Status.ShouldBe("skipped");
        gated.Reason.ShouldBe(reason);
        model.Requests.Count.ShouldBe(1);
    }
}
