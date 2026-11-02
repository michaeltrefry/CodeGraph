using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

/// <summary>
/// What only a person may do (CG-F16 E4), refused forbidden for a call made with an agent's token
/// before anything is written: record a person's correction or review as its source, approve or
/// reject a rule or a dreamer proposal, act in the project's humans' inbox, answer a question put
/// to the humans, and change the dreaming settings. Everything else is open to agents.
/// </summary>
internal static class AgentRefusal
{
    private const string Unchanged = " Nothing was changed.";

    public static void ThrowIfHumanOnly(object request)
    {
        switch (request)
        {
            case UpdateWorkRequest update:
                ThrowIfPersonSource(update.Corrections?.Select(correction => (CorrectionSourceKind?)correction.Source.Kind), "corrections");
                break;
            case EndSessionRequest end:
                ThrowIfPersonSource(end.Corrections?.Select(correction => (CorrectionSourceKind?)correction.Source.Kind), "corrections");
                break;
            case RecordRequest record:
                ThrowIfPersonSource([record.Entry?.Source?.Kind, record.Revise?.Source?.Kind], record.Entry is not null ? "entry.source" : "revise.source");
                break;
            case PromoteRequest { Action: PromoteAction.Approve or PromoteAction.Reject } promote:
                throw Forbidden($"An agent cannot {(promote.Action == PromoteAction.Approve ? "approve" : "reject")} a rule: that is the owner's decision in the review queue. " +
                    "Use promote propose to put the lesson in the review queue." + Unchanged);
            case DreamReviewRequest review:
                throw Forbidden($"An agent cannot {(review.Action == DreamReviewAction.Accept ? "accept" : "reject")} a dreamer proposal: the owner reviews proposals." + Unchanged);
            case InboxRequest { SessionId: null }:
                throw Forbidden("Without session_id inbox reads the project's humans' inbox, which an agent cannot read. Pass your session_id to read your own messages." + Unchanged);
            case AckRequest { SessionId: null }:
                throw Forbidden("Without session_id ack acknowledges the project's humans' messages, which an agent cannot do. Pass your session_id to acknowledge your own messages." + Unchanged);
        }
    }

    /// <summary>An agent answering or declining a question or request put to the project's humans.</summary>
    public static WorkException HumanQuestion() =>
        Forbidden("An agent cannot answer or decline a question or request put to the project's humans; only a person resolves it. " +
            "Reply without resolves to add information." + Unchanged);

    public static WorkException DreamSettings() =>
        Forbidden("An agent cannot change the dreaming settings; reading them is allowed." + Unchanged);

    private static void ThrowIfPersonSource(IEnumerable<CorrectionSourceKind?>? kinds, string argument)
    {
        if (kinds?.FirstOrDefault(kind => kind is CorrectionSourceKind.Prompt or CorrectionSourceKind.Review) is not { } kind)
            return;
        throw Forbidden(
            $"{argument}: an agent cannot record source {(kind == CorrectionSourceKind.Prompt ? "prompt" : "review")}, which is a person's own input. " +
            $"Relay what the owner said with source {(kind == CorrectionSourceKind.Prompt ? "relayed_prompt" : "relayed_review")}: it waits in the review queue for the owner to approve." + Unchanged);
    }

    private static WorkException Forbidden(string message) => new(WorkErrorCodes.Forbidden, message);
}
