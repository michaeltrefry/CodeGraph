using CodeGraph.Models.Work;

namespace CodeGraph.Data;

/// <summary>Read-only recurrence before/after each rule change, grounded in lesson_events (D37).</summary>
public interface IDreamingEffectsStore
{
    /// <returns>Null when the run does not belong to the requested project.</returns>
    Task<DreamEffects?> GetEffectsAsync(Guid projectId, Guid runId, CancellationToken ct = default);
}
