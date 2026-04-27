using CodeGraph.Data;

namespace CodeGraph.Services.Assistant;

public interface IAssistantRetentionCleanupService
{
    Task<AssistantRetentionCleanupResult> CleanupAsync(CancellationToken ct = default);
}
