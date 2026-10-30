namespace CodeGraph.Models.Work;

public sealed record DreamRunRequest(string Project,string Trigger="run_now");
public sealed record DreamRunsRequest(string Project,string? Id=null,string? Before=null,int Limit=30);
public sealed record DreamSettingsRequest(DreamSettings? Settings=null,string? ExpectedVersion=null);
public sealed record DreamInstructionsRequest(string Project,string? Scope=null,string? Content=null,int? ExpectedRevision=null);
public sealed record DreamRevertRequest(string Project,string Id);
