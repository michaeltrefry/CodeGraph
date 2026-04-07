using Shouldly;
using CodeGraph.Models.Messages;
using CodeGraph.Jobs.Jobs;

namespace CodeGraph.Jobs.Tests.Jobs;

public class ProcessRepositoriesJobTests
{
    [Fact]
    public async Task NoReposArgument_PublishesNothing()
    {
        var messageBus = new RecordingMessageBus();
        var job = new TestProcessRepositoriesJob(messageBus);

        await job.InvokeAsync(new StartJob { Args = [] });

        messageBus.PublishedMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParsesEntries_AndPublishesOneMessagePerRepo()
    {
        var messageBus = new RecordingMessageBus();
        var job = new TestProcessRepositoriesJob(messageBus);

        await job.InvokeAsync(new StartJob
        {
            Args = new Dictionary<string, string>
            {
                ["repos"] = "Orders.Api::https://gitlab.example.com/group/orders-api;Billing.Api::C:\\repos\\Billing.Api;Users.Api",
                ["shouldIndex"] = "false",
                ["shouldAnalyze"] = "true",
                ["skipIfUpToDate"] = "false"
            }
        });

        messageBus.PublishedMessages.Count.ShouldBe(3);

        var gitLabRepo = messageBus.PublishedMessages[0].ShouldBeOfType<ProcessRepository>();
        gitLabRepo.Name.ShouldBe("Orders.Api");
        gitLabRepo.RepoUrl.ShouldBe("https://gitlab.example.com/group/orders-api");
        string.IsNullOrEmpty(gitLabRepo.Path).ShouldBeTrue();
        gitLabRepo.ShouldIndex.ShouldBeFalse();
        gitLabRepo.ShouldAnalyze.ShouldBeTrue();
        gitLabRepo.SkipIfUpToDate.ShouldBeFalse();

        var localRepo = messageBus.PublishedMessages[1].ShouldBeOfType<ProcessRepository>();
        localRepo.Name.ShouldBe("Billing.Api");
        localRepo.Path.ShouldBe("C:\\repos\\Billing.Api");
        localRepo.RepoUrl.ShouldBeNull();

        var nameOnlyRepo = messageBus.PublishedMessages[2].ShouldBeOfType<ProcessRepository>();
        nameOnlyRepo.Name.ShouldBe("Users.Api");
        string.IsNullOrEmpty(nameOnlyRepo.Path).ShouldBeTrue();
        nameOnlyRepo.RepoUrl.ShouldBeNull();
    }
}
