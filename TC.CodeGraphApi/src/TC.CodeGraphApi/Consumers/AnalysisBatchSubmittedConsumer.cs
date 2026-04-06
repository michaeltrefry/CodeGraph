using MassTransit;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models.Messages;
using TC.CodeGraphApi.Services.Analyzers;
using TC.Common.Configuration;
using TC.Common.TcServiceStack.Queue;
using TC.Jarvis.DependencyInjection;

namespace TC.CodeGraphApi.Consumers;

/// <summary>
/// Checks an Anthropic batch for completion. If the batch isn't done yet,
/// throws BatchNotReadyException to trigger delayed redelivery via MassTransit.
/// Results are processed as soon as they're ready without waiting for the scheduled job.
/// </summary>
public class AnalysisBatchSubmittedConsumer : TcConsumer<AnalysisBatchSubmitted, AnalysisBatchSubmittedConsumer>
{
    private readonly IScope _scope;
    private readonly ILogger<AnalysisBatchSubmittedConsumer> _logger1;

    /// <summary>
    /// Checks an Anthropic batch for completion. If the batch isn't done yet,
    /// throws BatchNotReadyException to trigger delayed redelivery via MassTransit.
    /// Results are processed as soon as they're ready without waiting for the scheduled job.
    /// </summary>
    public AnalysisBatchSubmittedConsumer(IScope scope, 
        ITcConfiguration<CodeGraphServiceSettings> settings, 
        ILogger<AnalysisBatchSubmittedConsumer> logger) : base(logger)
    {
        _scope = scope;
        _logger1 = logger;
        TcConsumerConfigurator.QueueSuffix = typeof(AnalysisBatchSubmitted).FullName;
    }

    public override Action<IInstanceConfigurator<AnalysisBatchSubmittedConsumer>> InstanceConfigurator
    {
        get { return configurator =>
        {
            configurator.UseMessageRetry(retry => retry
                .Incremental(3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10))
                .Ignore<BatchNotReadyException>());

            configurator.UseDelayedRedelivery(redelivery => redelivery
                .Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(15))
                .Handle<BatchNotReadyException>());
        }; }
    }

    public override async Task Consume(AnalysisBatchSubmitted message, ConsumeContext<AnalysisBatchSubmitted> consumeContext)
    {
        var ct = consumeContext.CancellationToken;

        using var childScope = _scope.CreateChildScope();
        var batchService = childScope.GetInstance<IBatchAnalysisService>();

        // Attempt to process completed batches for this repo
        await batchService.ProcessCompletedBatchesAsync(message.RepoName, ct);

        // Check if the batch is still pending
        var store = childScope.GetInstance<Data.IGraphStore>();
        var pending = await store.GetPendingBatchesAsync(message.RepoName);
        var stillPending = pending.Any(b => b.AnthropicBatchId == message.AnthropicBatchId);

        if (stillPending)
        {
            _logger1.LogInformation(
                "Batch {BatchId} for {Repo} still processing — will retry via redelivery",
                message.AnthropicBatchId, message.RepoName);

            throw new BatchNotReadyException(
                $"Batch {message.AnthropicBatchId} for {message.RepoName} is still processing");
        }

        _logger1.LogInformation("Batch {BatchId} for {Repo} completed and processed",
            message.AnthropicBatchId, message.RepoName);
    }
}

public class BatchNotReadyException : Exception
{
    public BatchNotReadyException(string message) : base(message)
    {
    }

    public BatchNotReadyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
