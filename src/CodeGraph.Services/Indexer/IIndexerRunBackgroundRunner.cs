namespace CodeGraph.Services.Indexer;

public interface IIndexerRunBackgroundRunner
{
    Task EnqueueAsync(long runId, CancellationToken ct = default);
}
