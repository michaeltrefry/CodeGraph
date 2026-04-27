namespace CodeGraph.Services.Assistant;

public interface IAssistantRunBackgroundRunner
{
    Task EnqueueAsync(long runId, CancellationToken ct = default);
    Task<bool> CancelAsync(long runId, CancellationToken ct = default);
}
