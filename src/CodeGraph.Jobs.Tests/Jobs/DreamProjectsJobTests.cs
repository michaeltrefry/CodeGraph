using CodeGraph.Jobs.Jobs;
using Shouldly;

namespace CodeGraph.Jobs.Tests.Jobs;

public class DreamProjectsJobTests
{
    [Fact]
    public async Task SubmitsDurablyWithoutRunningModelOnJobHost()
    {
        var client = new RecordingIndexerClient();
        var job = new DreamProjectsJob(client);
        var result = await job.ExecuteAsync(new EmptyJobRequest(), "job:dream-1");
        result.Success.ShouldBeTrue();
        client.DreamProjectsCalls.ShouldBe(1);
        client.SubmissionKeys.ShouldBe(["job:dream-1"]);
    }

    [Fact]
    public async Task RequiresDurableSubmissionKey()
    {
        var client = new RecordingIndexerClient();
        await Should.ThrowAsync<InvalidOperationException>(() => new DreamProjectsJob(client).ExecuteAsync(new EmptyJobRequest()));
        client.DreamProjectsCalls.ShouldBe(0);
    }

    [Fact]
    public async Task PropagatesCancellationToIndexer()
    {
        var client = new RecordingIndexerClient();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => new DreamProjectsJob(client).ExecuteAsync(new EmptyJobRequest(), "job:dream-2", source.Token));
        client.DreamProjectsCalls.ShouldBe(0);
    }
}
