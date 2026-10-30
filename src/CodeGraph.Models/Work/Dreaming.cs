using System.Text.Json;

namespace CodeGraph.Models.Work;

public sealed record DreamSettings(bool Enabled = true, string DefaultProvider = "anthropic", string DefaultModel = "claude-sonnet-4-6",
    int MaxTurns = 12, int MaxTokensPerRun = 128000, int MinimumIntervalMinutes = 60, string Schedule = "0 2 * * *",
    decimal InputCostPerMillion = 3m, decimal OutputCostPerMillion = 15m, string? Version = null);

public sealed record DreamEvidence(string Kind, string Reference, string Summary);
public sealed record DreamAction(string Tool, JsonElement Arguments, JsonElement Result, IReadOnlyList<DreamEvidence> Evidence, DateTime CreatedAt);
public sealed record DreamRun(Guid Id, Guid ProjectId, string Trigger, WorkActor Initiator, string Status, DateTime StartedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] DateTime? EndedAt, string Provider, string Model, long InputTokens, long OutputTokens, decimal EstimatedCost,
    int BaseInstructionRevision, int ProjectInstructionRevision, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? SignalFingerprint, IReadOnlyList<DreamAction> Actions,
    string? Report = null, string? Reason = null)
{
    public IReadOnlyDictionary<string,int> ActionCounts => Actions
        .Where(a=>a.Tool is "dream_verify" or "dream_revise" or "dream_relate" or "dream_promote" or "dream_record" or "dream_challenge" or "dream_propose")
        .Where(a=>!a.Result.TryGetProperty("error",out _)).GroupBy(a=>a.Tool).ToDictionary(g=>g.Key,g=>g.Count());
}
public sealed record DreamInstructionRevision(int Revision, string Content, string EditedBy, DateTime CreatedAt);
public sealed record DreamInstructions(string Slug, string Content, int Revision, IReadOnlyList<DreamInstructionRevision> History);
public sealed record DreamInstructionSet(DreamInstructions Base, DreamInstructions Project);

/// <summary>A capability created by the run service, never deserialized from a caller's arguments.</summary>
public sealed class DreamExecutionScope : IDisposable
{
    private static readonly AsyncLocal<DreamExecutionScope?> Slot = new();
    private readonly DreamExecutionScope? previous;
    private readonly HashSet<string> sourceReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string,(string? Content,string? Index)> sourceHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<long,string> graphReads = new();
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
    public void MarkSourceRead(string repository, string path, long? nodeId = null, string? contentHash = null, string? indexHash = null)
    { sourceReads.Add($"file:{repository}:{path}"); sourceHashes[$"file:{repository}:{path}"]=(contentHash,indexHash); if (nodeId is { } id) sourceReads.Add($"node:{id}"); }
    public string? ReadFileHash(string repository,string path)=>sourceHashes.TryGetValue($"file:{repository}:{path}",out var proof)
        &&proof.Content is not null&&string.Equals(proof.Content,proof.Index,StringComparison.OrdinalIgnoreCase)?proof.Index:null;
    public bool ReadFile(string repository, string path) => sourceReads.Contains($"file:{repository}:{path}");
    public bool ReadNode(long id) => sourceReads.Contains($"node:{id}");
    public void MarkGraphRead(long id,string fingerprint) {sourceReads.Add($"node:{id}");graphReads[id]=fingerprint;}
    public string? ReadNodeHash(long id)=>graphReads.GetValueOrDefault(id);
    public void Dispose() => Slot.Value = previous;
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}

public static class DreamGraphIdentity
{
    public static string Fingerprint(long id,string repository,string label,string name,string qualifiedName,string path,int start,int end,string propertiesJson)
    {
        using var properties=JsonDocument.Parse(propertiesJson);
        var ordered=properties.RootElement.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).ToDictionary(p=>p.Name,p=>p.Value);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new {id,repository,label,name,qualifiedName,path,start,end,properties=ordered}))));
    }
}

public static class DreamPolicy
{
    public static bool IsDreamer(WorkActor actor) => actor.Kind == WorkActor.SystemKind && actor.Name == "dreamer";
    public static DreamExecutionScope RequireScope(WorkActor actor)
    {
        var scope = DreamExecutionScope.Current;
        if (!IsDreamer(actor) || scope is null || actor.SessionId != scope.RunId.ToString() || actor.Model != scope.Model)
            throw new WorkException(WorkErrorCodes.Forbidden,"The dreamer requires an active, matching server run. Nothing was changed.");
        return scope;
    }
    public static void RequireCurrentProject(Guid projectId)
    { if (DreamExecutionScope.Current is {} scope) RequireProject(scope.Actor,projectId); }
    public static void RequireProject(WorkActor actor, Guid projectId)
    { if ((IsDreamer(actor)||DreamExecutionScope.Current is not null) && RequireScope(actor).ProjectId != projectId) throw new WorkException(WorkErrorCodes.Forbidden,"The dreamer may access only its run's project. Nothing was changed."); }
    public static void RefuseAmbient(string operation)
    {if(DreamExecutionScope.Current is {} scope)Refuse(scope.Actor,operation);}
    public static void Refuse(WorkActor actor, string operation)
    { if (IsDreamer(actor)||DreamExecutionScope.Current is not null) throw new WorkException(WorkErrorCodes.Forbidden,$"The dreamer may not {operation}. Nothing was changed."); }
    public static void RequireWrite(WorkActor actor, string operation)
    {
        if (!IsDreamer(actor)&&DreamExecutionScope.Current is null) return;
        var scope = RequireScope(actor);
        if (scope.Evidence.Count == 0 || scope.Evidence.Any(e => string.IsNullOrWhiteSpace(e.Reference) || string.IsNullOrWhiteSpace(e.Summary)))
            throw new WorkException(WorkErrorCodes.Forbidden,$"Dreamer {operation} requires evidence references and what they demonstrate. Nothing was changed.");
    }
    public static void RequireVerification(WorkActor actor, KnowledgeView entry)
    {
        if (!IsDreamer(actor)) return;
        RequireProject(actor, entry.ProjectId); RequireWrite(actor, "verify");
        var scope = RequireScope(actor);
        var files = entry.VerifiedFiles ?? [];
        var nodes = entry.Nodes ?? [];
        if (files.Count == 0 && nodes.Count == 0 || files.Any(f => !scope.ReadFile(f.Repository, f.Path)) || nodes.Any(n => !scope.ReadNode(n.Id)))
            throw new WorkException(WorkErrorCodes.Forbidden,"Dreamer verify requires reading every referenced graph node and source file in this run. Nothing was changed.");
    }
}
