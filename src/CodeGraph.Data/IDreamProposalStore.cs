using CodeGraph.Models.Work;
namespace CodeGraph.Data;

public interface IDreamProposalStore
{
    Task<DreamProposalResult> CreateDreamProposalAsync(Guid projectId, Guid runId, DreamProposalSpec spec, WorkActor actor, CancellationToken ct = default);
    Task<DreamProposalView?> GetDreamProposalAsync(Guid projectId, Guid id, CancellationToken ct = default);
    Task<DreamReviewResult> ReviewDreamProposalAsync(Guid projectId, Guid id, long expectedVersion, DreamReviewAction action, string? reason, WorkActor actor, CancellationToken ct = default);
}

public sealed record DreamProposalSpec(DreamProposalAction Action, Guid Target, long ExpectedVersion, string Rationale, string Prevalence,
    IReadOnlyList<DreamEvidenceInput> Evidence, Guid? Other = null, long? OtherVersion = null, KnowledgeSpec? Replacement = null,
    KnowledgeEntryInput? ReplacementInput = null, string? Draft = null);
