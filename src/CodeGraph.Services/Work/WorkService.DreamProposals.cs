using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public sealed partial class WorkService
{
    private IDreamProposalStore Proposals => Knowledge as IDreamProposalStore ?? throw new WorkStoreUnavailableException();

    private async Task<object> DreamProposeAsync(DreamProposalRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        var project = await KnowledgeProjectAsync(request.Project, session, ct);
        if (actor.Kind != WorkActor.SystemKind || actor.Name != "dreamer" || !Guid.TryParse(request.RunId, out var runId) || actor.SessionId != runId.ToString())
            throw WorkException.Invalid("Only the dreamer actor of the named run can submit proposals.");
        DreamPolicy.RequireProject(actor, project.Id);
        if (DreamPolicy.RequireScope(actor).RunId != runId) throw WorkException.Invalid("The proposal must name its server run.");
        if (!Guid.TryParse(request.Target, out var target)) throw WorkException.Invalid("target must be a knowledge entry id.");
        Guid? other = null;
        if (request.Other is { } value) other = Guid.TryParse(value, out var parsed) ? parsed : throw WorkException.Invalid("other must be a lesson id.");
        KnowledgeSpec? replacement = null;
        if (request.Replacement is { } input)
        {
            if (input.Supersedes is not null || input.SameAs is not null || input.Contradicts is not null)
                throw WorkException.Invalid("replacement must not embed lifecycle actions.");
            replacement = await ReadEntryAsync(input, project, "replacement", allowLinks: true, ct);
        }
        return await Proposals.CreateDreamProposalAsync(project.Id, runId, new DreamProposalSpec(request.Action, target, request.ExpectedVersion,
            Text(request.Rationale, "rationale", 1000), Text(request.Prevalence, "prevalence", 1000), request.Evidence,
            other, request.OtherExpectedVersion, replacement, request.Replacement, request.Draft), actor, ct);
    }

    private async Task<object> DreamReviewAsync(DreamReviewRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        DreamPolicy.Refuse(actor, "accept or reject proposals");
        var project = await KnowledgeProjectAsync(request.Project, session, ct);
        if (!Guid.TryParse(request.Id, out var id)) throw WorkException.Invalid("id must be a dream proposal id.");
        var reason = request.Reason is null ? null : Text(request.Reason, "reason", 1000);
        if (request.Action == DreamReviewAction.Reject && reason is null) throw WorkException.Invalid("Rejecting a proposal requires a reason.");
        return await Proposals.ReviewDreamProposalAsync(project.Id, id, request.ExpectedVersion, request.Action, reason, actor, ct);
    }
}
