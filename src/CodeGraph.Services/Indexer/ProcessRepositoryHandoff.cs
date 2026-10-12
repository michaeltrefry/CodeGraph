using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Indexer;

/// <summary>
/// Turns a ProcessRepository message into a durable indexer run, so indexing never runs while a
/// RabbitMQ delivery is held: a full index can outlast the broker's consumer_timeout (30 min by
/// default), after which the broker closes the channel, cancels the consume and redelivers the
/// message, forever (sc-25698). The durable run is leased and heartbeated instead.
/// A message whose identical run is already queued or running coalesces into that run.
/// </summary>
public sealed class ProcessRepositoryHandoff(
    IIndexerRunStore runStore,
    IIndexerRunBackgroundRunner backgroundRunner,
    ILogger<ProcessRepositoryHandoff> logger)
{
    public const string RequestedBy = "process-repository";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IndexerRunSubmissionResult> HandOffAsync(ProcessRepository message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Name);
        var argsJson = JsonSerializer.Serialize(message, JsonOptions);
        var submitted = await runStore.CreateOrCoalesceActiveIndexerRunAsync(new IndexerRunEntity
        {
            Operation = IndexerRunOperations.ProcessRepository,
            RequestedByUsername = RequestedBy,
            Target = message.Name,
            ArgsJson = argsJson,
            Status = "queued",
            Message = $"Queued processing for {message.Name}.",
            RetrySafe = IndexerRunOperations.IsRetrySafe(IndexerRunOperations.ProcessRepository),
            SubmissionHash = StandaloneIndexerOperationsService.ComputeSubmissionHash(
                IndexerRunOperations.ProcessRepository, message.Name, argsJson),
            CreatedAt = DateTime.UtcNow
        }, ct);

        if (submitted.Created)
        {
            logger.LogInformation(
                "Handed off ProcessRepository for {Repo} to durable indexer run {RunId}",
                message.Name, submitted.RunId);
            await backgroundRunner.EnqueueAsync(submitted.RunId, ct);
        }
        else
        {
            logger.LogInformation(
                "Coalesced ProcessRepository for {Repo} into active durable indexer run {RunId}",
                message.Name, submitted.RunId);
        }

        return submitted;
    }
}
