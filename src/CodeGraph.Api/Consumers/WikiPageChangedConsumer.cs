using CodeGraph.Models.Messages;
using CodeGraph.Services.WikiRag;
using MassTransit;

namespace CodeGraph.Api.Consumers;

public class WikiPageChangedConsumer(
    IConventionEmbeddingService conventionEmbeddingService,
    ILogger<WikiPageChangedConsumer> logger) : IConsumer<WikiPageChanged>
{
    public async Task Consume(ConsumeContext<WikiPageChanged> context)
    {
        var message = context.Message;
        if (!message.SectionSlug.Equals("conventions", StringComparison.OrdinalIgnoreCase))
            return;

        var deleted = message.ChangeType.Equals("deleted", StringComparison.OrdinalIgnoreCase);
        var chunks = await conventionEmbeddingService.ReindexPageAsync(message.PageId, deleted, context.CancellationToken);
        logger.LogInformation(
            "Processed wiki page change {ChangeType} for {SectionSlug}/{PageSlug} v{Revision}; indexed {ChunkCount} chunks",
            message.ChangeType,
            message.SectionSlug,
            message.PageSlug,
            message.Revision,
            chunks);
    }
}
