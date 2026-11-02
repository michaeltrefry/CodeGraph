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
            case ImportKnowledgeRequest import:
                ThrowIfPersonSource(import.Candidates.Select(candidate => candidate.Entry.Source?.Kind), "candidates[].entry.source");
                break;
            case InboxRequest { SessionId: null }:
                throw Forbidden("Without session_id inbox reads the project's humans' inbox, which an agent cannot read. Pass your session_id to read your own messages." + Unchanged);
            case AckRequest { SessionId: null }:
                throw Forbidden("Without session_id ack acknowledges the project's humans' messages, which an agent cannot do. Pass your session_id to acknowledge your own messages." + Unchanged);
        }
    }

    /// <summary>
    /// A lesson recorded from a person's prompt or review keeps its authority only as the person
    /// wrote it: an agent may not revise or retract it, nor merge it (relate same_as) into another lesson,
    /// which would pass its rule status on to the agent's words. A challenge is the agent's way.
    /// </summary>
    public static void ThrowIfReshapingPersonsLesson(RecordRequest request, KnowledgeView current)
    {
        if (current.Details?.Source?.Kind is not (CorrectionSourceKind.Prompt or CorrectionSourceKind.Review))
            return;
        if (request.Revise is not null || request.Retract is not null)
            throw Forbidden($"Lesson {current.Id} is a person's own correction; an agent cannot {(request.Revise is not null ? "revise" : "retract")} it. Challenge it with your evidence instead." + Unchanged);
        if (request.Relate?.SameAs is not null)
            throw Forbidden($"Lesson {current.Id} is a person's own correction; an agent cannot merge it into another lesson. " +
                "Merge your lesson into it (relate with your lesson's id and same_as this one), or challenge it." + Unchanged);
    }

    /// <summary>A new entry superseding a person's lesson would take over its rule status in the agent's words.</summary>
    public static void ThrowIfReplacingPersonsLesson(KnowledgeView replaced)
    {
        if (replaced.Details?.Source?.Kind is CorrectionSourceKind.Prompt or CorrectionSourceKind.Review)
            throw Forbidden($"Lesson {replaced.Id} is a person's own correction; an agent cannot supersede it with its own wording. Challenge it with your evidence instead." + Unchanged);
    }

    /// <summary>Amending or retracting a person's lesson through a challenge is the owner's call; upholding it changes nothing.</summary>
    public static void ThrowIfChangingPersonsLesson(ChallengeOutcome outcome, KnowledgeView lesson)
    {
        if (outcome == ChallengeOutcome.Uphold || lesson.Details?.Source?.Kind is not (CorrectionSourceKind.Prompt or CorrectionSourceKind.Review))
            return;
        throw Forbidden($"Lesson {lesson.Id} is a person's own correction; an agent cannot {(outcome == ChallengeOutcome.Amend ? "amend" : "retract")} it by resolving a challenge. " +
            "The challenge stays open in the review queue for the owner." + Unchanged);
    }

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
