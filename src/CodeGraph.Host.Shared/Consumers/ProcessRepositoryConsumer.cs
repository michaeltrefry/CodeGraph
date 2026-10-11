using MassTransit;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Pipeline;

namespace CodeGraph.Host.Shared.Consumers;

public class ProcessRepositoryConsumer(
    IProjectService projectService,
    ILogger<ProcessRepositoryConsumer> logger) : IConsumer<ProcessRepository>
{
    public async Task Consume(ConsumeContext<ProcessRepository> context)
    {
        logger.LogInformation("Processing repository {Repo}", context.Message.Name);
        try
        {
            await projectService.ProcessRepository(context.Message, context.CancellationToken);
        }
        catch (Exception ex)
        {
            // Every failed consume is reported here with its repository and phase before MassTransit
            // retries it; the retry/redelivery middleware alone only logs a Warning without context.
            logger.LogError(ex,
                "ProcessRepository failed for {Repo} in phase {Phase}{Cancelled} (retry attempt {RetryAttempt}, redelivery {RedeliveryCount})",
                context.Message.Name,
                IndexingFailure.GetPhase(ex),
                ex is OperationCanceledException ? " (cancelled)" : "",
                context.GetRetryAttempt(),
                context.GetRedeliveryCount());
            throw;
        }
    }
}
