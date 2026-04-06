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
                ["repos"] = "TC.OrdersApi::https://gitlab.tcdevops.com/group/TC.OrdersApi;TC.BillingApi::C:\\repos\\TC.BillingApi;TC.UsersApi",
                ["shouldIndex"] = "false",
                ["shouldAnalyze"] = "true",
                ["skipIfUpToDate"] = "false"
            }
        });

        messageBus.PublishedMessages.Count.ShouldBe(3);

        var gitLabRepo = messageBus.PublishedMessages[0].ShouldBeOfType<ProcessRepository>();
        gitLabRepo.Name.ShouldBe("TC.OrdersApi");
        gitLabRepo.GitLabUrl.ShouldBe("https://gitlab.tcdevops.com/group/TC.OrdersApi");
        string.IsNullOrEmpty(gitLabRepo.Path).ShouldBeTrue();
        gitLabRepo.ShouldIndex.ShouldBeFalse();
        gitLabRepo.ShouldAnalyze.ShouldBeTrue();
        gitLabRepo.SkipIfUpToDate.ShouldBeFalse();

        var localRepo = messageBus.PublishedMessages[1].ShouldBeOfType<ProcessRepository>();
        localRepo.Name.ShouldBe("TC.BillingApi");
        localRepo.Path.ShouldBe("C:\\repos\\TC.BillingApi");
        localRepo.GitLabUrl.ShouldBeNull();

        var nameOnlyRepo = messageBus.PublishedMessages[2].ShouldBeOfType<ProcessRepository>();
        nameOnlyRepo.Name.ShouldBe("TC.UsersApi");
        string.IsNullOrEmpty(nameOnlyRepo.Path).ShouldBeTrue();
        nameOnlyRepo.GitLabUrl.ShouldBeNull();
    }
}
