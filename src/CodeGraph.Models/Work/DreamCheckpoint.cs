using System.Text.Json;

namespace CodeGraph.Models.Work;

/// <summary>Private continuation state; intentionally separate from public run reports.</summary>
public sealed record DreamCheckpoint(Guid ProjectId, Guid RunId, JsonElement State);

/// <summary>A committed write or proposal, even when its tool response was never checkpointed.</summary>
public sealed record DreamExecutionOutcome(Guid RunId, JsonElement Result);

public sealed record DreamRecoveredAction(Guid RunId, DreamAction Action);
