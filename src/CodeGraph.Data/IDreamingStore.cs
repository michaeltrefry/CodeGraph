using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Data;

public interface IDreamingStore
{
    Task<IAsyncDisposable?> TryAcquireAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListProjectIdsAsync(CancellationToken ct = default);
    Task SaveRunAsync(DreamRun run, CancellationToken ct = default);
    Task<DreamCheckpoint?> GetCheckpointAsync(Guid projectId, CancellationToken ct = default);
    Task SaveProgressAsync(DreamRun run, JsonElement? checkpoint, Guid? expectedCheckpointRunId, CancellationToken ct = default);
    Task<DreamExecutionOutcome?> GetExecutionResultAsync(Guid projectId, Guid executionId, CancellationToken ct = default);
    Task<DreamRun?> GetRunAsync(Guid projectId, Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<DreamRun>> ListRunsBeforeAsync(Guid projectId, Guid? before, int limit, CancellationToken ct = default);
    Task<DreamRun?> LastCompletedAsync(Guid projectId, CancellationToken ct = default);
    Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default);
    Task<DreamSettings> SetSettingsAsync(DreamSettings settings, WorkActor actor, CancellationToken ct = default);
    Task<DreamInstructionSet> GetInstructionsAsync(Guid projectId, CancellationToken ct = default);
    Task<DreamInstructions> SetInstructionsAsync(Guid projectId, string scope, string content, int expectedRevision, WorkActor actor, CancellationToken ct = default);
}
