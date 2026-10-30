using System.ComponentModel;

namespace CodeGraph.Models.Work;

public enum DreamProposalAction { Retract, Supersede, Merge, SkillDraft, HookDraft, TestDraft }
public enum DreamProposalStatus { Pending, Accepted, Rejected }
public enum DreamEvidenceKind { Entry, LessonEvent, Item, ReviewEvidence, GraphNode }
public enum DreamReviewAction { Accept, Reject }

public sealed record DreamEvidenceInput(DreamEvidenceKind Kind, string Id);
public sealed record DreamEvidenceView(DreamEvidenceKind Kind, string Id, string Fingerprint, DateTime ObservedAt);

public sealed class DreamProposalRequest : ISessionArguments
{
    public string? SessionId { get; init; }
    public required string Project { get; init; }
    public required string RunId { get; init; }
    public required DreamProposalAction Action { get; init; }
    public required string Target { get; init; }
    public required long ExpectedVersion { get; init; }
    public required string Rationale { get; init; }
    public required string Prevalence { get; init; }
    public required List<DreamEvidenceInput> Evidence { get; init; }
    [Description("Merge destination; required with action merge.")]
    public string? Other { get; init; }
    public long? OtherExpectedVersion { get; init; }
    [Description("Replacement entry; required only with action supersede. No embedded supersedes, same_as or contradicts.")]
    public KnowledgeEntryInput? Replacement { get; init; }
    [Description("Proposed skill, hook or test text. Stored for application outside CodeGraph; never executed or written to a file.")]
    public string? Draft { get; init; }
}

public sealed class DreamReviewRequest : ISessionArguments
{
    public string? SessionId { get; init; }
    public required string Project { get; init; }
    public required string Id { get; init; }
    public required long ExpectedVersion { get; init; }
    public required DreamReviewAction Action { get; init; }
    public string? Reason { get; init; }
}

public sealed record DreamProposalView(Guid Id, Guid ProjectId, Guid RunId, DreamProposalAction Action, Guid Target, long TargetVersion,
    string Rationale, string Prevalence, IReadOnlyList<DreamEvidenceView> Evidence, DreamProposalStatus Status, long Version,
    WorkActor Author, DateTime CreatedAt, Guid? Other = null, long? OtherVersion = null, string? Draft = null,
    KnowledgeEntryInput? Replacement = null, string? ReviewReason = null, WorkActor? ReviewedBy = null, DateTime? ReviewedAt = null);
public sealed record DreamProposalResult(bool Created, bool Suppressed, DreamProposalView? Proposal);
public sealed record DreamReviewResult(DreamProposalView Proposal, KnowledgeView? Entry = null);
