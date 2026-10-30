using System.Text.Json;

namespace CodeGraph.Models.Work;

public sealed record DreamSettings(bool Enabled = false, string DefaultProvider = "anthropic", string DefaultModel = "claude-sonnet-4-6",
    int MaxTurns = 12, int MaxTokensPerRun = 24000, int MinimumIntervalMinutes = 60, string Schedule = "0 0 2 * * *",
    decimal InputCostPerMillion = 3m, decimal OutputCostPerMillion = 15m);

public sealed record DreamEvidence(string Kind, string Reference, string Summary);
public sealed record DreamAction(string Tool, JsonElement Arguments, JsonElement Result, IReadOnlyList<DreamEvidence> Evidence, DateTime CreatedAt);
public sealed record DreamRun(Guid Id, Guid ProjectId, string Trigger, WorkActor Initiator, string Status, DateTime StartedAt,
    DateTime? EndedAt, string Provider, string Model, long InputTokens, long OutputTokens, decimal EstimatedCost,
    int BaseInstructionRevision, int ProjectInstructionRevision, string? SignalFingerprint, IReadOnlyList<DreamAction> Actions,
    string? Report = null, string? Reason = null);
public sealed record DreamInstructionRevision(int Revision, string Content, string EditedBy, DateTime CreatedAt);
public sealed record DreamInstructions(string Slug, string Content, int Revision, IReadOnlyList<DreamInstructionRevision> History);
public sealed record DreamInstructionSet(DreamInstructions Base, DreamInstructions Project);

/// <summary>A capability created by the run service, never deserialized from a caller's arguments.</summary>
public sealed class DreamExecutionScope : IDisposable
{
    private static readonly AsyncLocal<DreamExecutionScope?> Slot = new();
    private readonly DreamExecutionScope? previous;
    private readonly HashSet<string> sourceReads = new(StringComparer.Ordinal);
    public static DreamExecutionScope? Current => Slot.Value;
    public Guid RunId { get; }
    public Guid ProjectId { get; }
    public string Model { get; }
    public WorkActor Actor => new("dreamer", WorkActor.SystemKind, RunId.ToString(), Model: Model);
    public IReadOnlyList<DreamEvidence> Evidence { get; private set; } = [];
    private DreamExecutionScope(Guid runId, Guid projectId, string model)
    { RunId = runId; ProjectId = projectId; Model = model; previous = Slot.Value; Slot.Value = this; }
    public static DreamExecutionScope Begin(Guid runId, Guid projectId, string model) => new(runId, projectId, model);
    public IDisposable UseEvidence(IReadOnlyList<DreamEvidence> evidence)
    { var old = Evidence; Evidence = evidence; return new Restore(() => Evidence = old); }
    public void MarkSourceRead(string repository, string path, long? nodeId = null)
    { sourceReads.Add($"file:{repository}:{path}"); if (nodeId is { } id) sourceReads.Add($"node:{id}"); }
    public bool ReadFile(string repository, string path) => sourceReads.Contains($"file:{repository}:{path}");
    public bool ReadNode(long id) => sourceReads.Contains($"node:{id}");
    public void Dispose() => Slot.Value = previous;
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}

public static class DreamPolicy
{
    public static bool IsDreamer(WorkActor actor) => actor.Kind == WorkActor.SystemKind && actor.Name == "dreamer";
    public static DreamExecutionScope RequireScope(WorkActor actor)
    {
        var scope = DreamExecutionScope.Current;
        if (!IsDreamer(actor) || scope is null || actor.SessionId != scope.RunId.ToString() || actor.Model != scope.Model)
            throw WorkException.Invalid("The dreamer requires an active, matching server run. Nothing was changed.");
        return scope;
    }
    public static void RequireProject(WorkActor actor, Guid projectId)
    { if (IsDreamer(actor) && RequireScope(actor).ProjectId != projectId) throw WorkException.Invalid("The dreamer may access only its run's project. Nothing was changed."); }
    public static void Refuse(WorkActor actor, string operation)
    { if (IsDreamer(actor)) throw WorkException.Invalid($"The dreamer may not {operation}. Nothing was changed."); }
    public static void RequireWrite(WorkActor actor, string operation)
    {
        if (!IsDreamer(actor)) return;
        var scope = RequireScope(actor);
        if (scope.Evidence.Count == 0 || scope.Evidence.Any(e => string.IsNullOrWhiteSpace(e.Reference) || string.IsNullOrWhiteSpace(e.Summary)))
            throw WorkException.Invalid($"Dreamer {operation} requires evidence references and what they demonstrate. Nothing was changed.");
    }
    public static void RequireVerification(WorkActor actor, KnowledgeView entry)
    {
        if (!IsDreamer(actor)) return;
        RequireProject(actor, entry.ProjectId); RequireWrite(actor, "verify");
        var scope = RequireScope(actor);
        var files = entry.VerifiedFiles ?? [];
        var nodes = entry.Nodes ?? [];
        if (files.Count == 0 && nodes.Count == 0 || files.Any(f => !scope.ReadFile(f.Repository, f.Path)) || nodes.Any(n => !scope.ReadNode(n.Id)))
            throw WorkException.Invalid("Dreamer verify requires reading every referenced graph node and source file in this run. Nothing was changed.");
    }
}
