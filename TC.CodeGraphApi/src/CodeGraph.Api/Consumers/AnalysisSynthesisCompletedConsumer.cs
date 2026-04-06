using MassTransit;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Analyzers;

namespace CodeGraph.Api.Consumers;

/// <summary>
/// Writes CODEGRAPH.md files to the repo after analysis synthesis completes.
/// Isolated from synthesis so Git I/O failures can retry independently.
/// </summary>
public class AnalysisSynthesisCompletedConsumer(
    IBatchAnalysisService batchService,
    ILogger<AnalysisSynthesisCompletedConsumer> logger) : IConsumer<AnalysisSynthesisCompleted>
{
    public async Task Consume(ConsumeContext<AnalysisSynthesisCompleted> context)
    {
        var message = context.Message;
        logger.LogInformation("Writing CODEGRAPH.md files for {Repo}", message.RepoName);
        await batchService.WriteCodeGraphDocsAsync(message.RepoName, context.CancellationToken);
    }
}
