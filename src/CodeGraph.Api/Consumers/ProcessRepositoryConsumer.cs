using MassTransit;
using CodeGraph.Models.Messages;
using CodeGraph.Services;

namespace CodeGraph.Api.Consumers;

public class ProcessRepositoryConsumer(
    IProjectService projectService,
    ILogger<ProcessRepositoryConsumer> logger) : IConsumer<ProcessRepository>
{
    public async Task Consume(ConsumeContext<ProcessRepository> context)
    {
        logger.LogInformation("Processing repository {Repo}", context.Message.Name);
        await projectService.ProcessRepository(context.Message, context.CancellationToken);
    }
}
