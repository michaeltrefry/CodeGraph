using System.Globalization;
using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

// Lessons, rules and challenges (S9, design §5.7): promote, challenge, lessons, review_queue, record
// relate / acknowledge, the similar lessons of a write, and the briefing's rules section. Arguments
// are read strictly here; the store checks state. No model call anywhere (E5); nothing here gates
// work (E6): a challenged rule stays active, the review queue is data only, and every operation is
// open to agents and people alike (no human-only operation).
public sealed partial class WorkService
{
    private const string LessonsCursorKind = "ls";
    private const string ReviewCursorKind = "rq";

    // ---- Similar lessons of a write ------------------------------------------------------------

    /// <summary>
    /// The similar lessons of the lessons a write stored or merged into (candidates for same_as /
    /// contradicts), or null. Embeddings find the candidates when an embedding provider is available
    /// (E5: embeddings, no model call); the lexical score otherwise. Nothing is merged on similarity.
    /// </summary>
    private async Task<IReadOnlyList<SimilarLessons>?> SimilarAsync(Guid projectId, IReadOnlyList<KnowledgeRow>? recorded, CancellationToken ct)
    {
        var lessonIds = (recorded ?? []).Where(row => row.Kind == KnowledgeKind.Lesson).Select(row => row.Id).Distinct().ToList();
        if (lessonIds.Count == 0)
            return null;
        var similar = await Knowledge.FindSimilarLessonsAsync(projectId, lessonIds, Embedder(), ct);
        return similar.Count == 0 ? null : similar;
    }

    private LessonEmbedder? Embedder()
    {
        if (embeddings is not { IsAvailable: true } provider)
            return null;
        return new LessonEmbedder(provider.ModelName, texts => provider.GenerateEmbeddings(texts), lessons.EmbeddingCandidateThreshold);
    }

    /// <summary>
    /// Data for the caller about lessons it stored that stay task-scoped: a correction from the
    /// human's prompt is a rule at once only with an applies-when activity (R24).
    /// </summary>
    private static IReadOnlyList<string>? LessonHints(IReadOnlyList<KnowledgeSpec> specs, IReadOnlyList<KnowledgeRow>? recorded)
    {
        var lessonSpecs = specs.Where(spec => spec.Kind == KnowledgeKind.Lesson).ToList();
        var lessonRows = (recorded ?? []).Where(row => row.Kind == KnowledgeKind.Lesson).ToList();
        if (lessonSpecs.Count == 0 || lessonSpecs.Count != lessonRows.Count)
            return null;
        var hints = lessonSpecs.Zip(lessonRows)
            .Where(pair => pair.First.Details?.Source?.Kind == CorrectionSourceKind.Prompt &&
                           pair.First.Details.AppliesWhen?.Activity is not { Count: > 0 } && pair.Second.Rule != true)
            .Select(pair => $"Lesson {pair.Second.Id} (from the human's prompt) is stored task-scoped, not as a rule: it has no applies_when.activity. " +
                            "Add applies_when.activity (record revise) to make it a rule served in briefings.")
            .Distinct()
            .ToList();
        return hints.Count == 0 ? null : hints;
    }

    private static KnowledgeRow ToRow(KnowledgeView view) => new(view.Id, view.Kind, view.Summary, view.Confidence, view.Version);

    private static Guid? ReadLessonId(string? value, string argument) =>
        value is null
            ? null
            : Guid.TryParse(value.Trim(), out var id)
                ? id
                : throw WorkException.Invalid($"{argument} must be the id of a lesson. Nothing was changed.");

    private static (Guid? SameAs, Guid? Contradicts) ReadLessonDecision(string? sameAs, string? contradicts, string where)
    {
        if (sameAs is not null && contradicts is not null)
            throw WorkException.Invalid($"{where} passes both same_as and contradicts: a lesson either says the same as another or contradicts it. Nothing was changed.");
        return (ReadLessonId(sameAs, $"{where}.same_as"), ReadLessonId(contradicts, $"{where}.contradicts"));
    }

    /// <summary>Skill names as stored: trimmed, distinct ignoring case, at most 50 of at most 255 characters; null when not passed.</summary>
    private static IReadOnlyList<string>? ReadSkills(IReadOnlyList<string>? skills, string argument)
    {
        if (skills is null)
            return null;
        if (skills.Count > LessonLimits.MaxSkills)
            throw WorkException.Invalid($"{argument} holds at most {LessonLimits.MaxSkills} skill names. Nothing was changed.");
        return skills.Select((skill, index) => Text(skill, $"{argument}[{index}]", LessonLimits.MaxNameLength))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Briefing rules -----------------------------------------------------------------------------

    /// <summary>
    /// The default activity of a session that declared none (B2, documented on start_session's
    /// activity): derived from the status of the items it holds, deterministically: in_progress
    /// means implementing, in_review means reviewing. Any other status implies nothing.
    /// </summary>
    internal static IReadOnlyList<LessonActivity> DerivedActivity(IEnumerable<WorkStatus> held) =>
        held.Select(status => status switch
            {
                WorkStatus.InProgress => (LessonActivity?)LessonActivity.Implementing,
                WorkStatus.InReview => LessonActivity.Reviewing,
                _ => null
            })
            .OfType<LessonActivity>()
            .Distinct()
            .Order()
            .ToList();

    /// <summary>
    /// The briefing with its rules section: the rules whose applies-when matches the session (its
    /// activity, repository, paths, harness) and that no local fix carries for it (its skills, its
    /// harness's instructions and hooks, any test or CI gate), each with its contradictions and open
    /// challenges. Without a declared activity the activity is derived from the items the session
    /// holds (derived_activity); without either no rule matches, and rule_activities names the
    /// activities the project has rules for.
    /// </summary>
    private async Task<BriefingView> WithRulesAsync(BriefingView briefing, SessionView session, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var activity = session.Activity ?? [];
        IReadOnlyList<LessonActivity>? derived = null;
        if (activity.Count == 0)
        {
            derived = DerivedActivity((briefing.MyWork ?? []).Select(item => item.Status));
            activity = derived;
        }

        if (activity.Count == 0)
        {
            var available = await Knowledge.ReadRuleActivitiesAsync(session.ProjectId, ct);
            return available.Count == 0 ? briefing : briefing with { RuleActivities = available };
        }

        var rules = await Knowledge.ReadRulesAsync(
            new RuleContext(session.ProjectId, activity, session.Repository, paths, session.Harness, LessonLimits.MaxBriefingRules, session.Skills), ct);
        if (derived is { Count: > 0 })
            briefing = briefing with { DerivedActivity = derived };
        if (rules.Count == 0)
            return briefing;
        // A rule linked to the session's item is shown once, among the rules.
        var ruleIds = rules.Select(rule => rule.Id).ToHashSet();
        var knowledge = briefing.Knowledge?.Where(row => !ruleIds.Contains(row.Id)).ToList();
        return briefing with { Rules = rules, Knowledge = knowledge is { Count: > 0 } ? knowledge : null };
    }

    // ---- promote -------------------------------------------------------------------------------------

    private async Task<object> PromoteAsync(PromoteRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        var lesson = await RequireEntryAsync(request.Id, "id", null, session, ct);
        if (lesson.Kind != KnowledgeKind.Lesson)
            throw WorkException.Invalid($"Entry {lesson.Id} is a {WorkJson.Name(lesson.Kind)}; promote takes lessons. Nothing was changed.");

        var reason = Optional(request.Reason, "reason", KnowledgeLimits.MaxReasonLength);
        AddressedBySpec? addressedBy = null;
        switch (request.Action)
        {
            case PromoteAction.AddressedBy when request.AddressedBy is null:
                throw WorkException.Invalid("action addressed_by needs addressed_by {kind, name, ref, harness}: what addresses the lesson. Nothing was changed.");
            case PromoteAction.AddressedBy:
                var by = request.AddressedBy;
                var carriedByHarness = by.Kind is AddressedByKind.Instruction or AddressedByKind.Hook;
                if (carriedByHarness && by.Harness is null)
                    throw WorkException.Invalid($"addressed_by of kind {WorkJson.Name(by.Kind)} needs harness: it hides the rule only from sessions of that harness. Nothing was changed.");
                if (!carriedByHarness && by.Harness is not null)
                    throw WorkException.Invalid($"addressed_by.harness goes with kind instruction or hook; a skill is scoped by the sessions that load it, a test or ci gate covers every session. Nothing was changed.");
                addressedBy = new AddressedBySpec(by.Kind,
                    Text(by.Name, "addressed_by.name", LessonLimits.MaxNameLength),
                    Optional(by.Ref, "addressed_by.ref", KnowledgeLimits.MaxSourceRefLength),
                    by.Harness is { } harness ? Text(harness, "addressed_by.harness", WorkSessionLimits.MaxHarnessLength).ToLowerInvariant() : null);
                break;
            case PromoteAction.Reject when reason is null:
                throw WorkException.Invalid("reject needs reason: it is recorded so the same lesson is not proposed again. Nothing was changed.");
            default:
                if (request.AddressedBy is not null)
                    throw WorkException.Invalid($"addressed_by goes with action addressed_by, not {WorkJson.Name(request.Action)}. Nothing was changed.");
                break;
        }

        return await Knowledge.PromoteLessonAsync(lesson.Id, request.ExpectedVersion, request.Action, reason, addressedBy, actor, ct);
    }

    // ---- challenge ---------------------------------------------------------------------------------

    private async Task<object> ChallengeAsync(ChallengeRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        if ((request.Open is null) == (request.Resolve is null))
            throw WorkException.Invalid("challenge takes exactly one of open (dispute a rule with evidence) and resolve (uphold, amend or retract it). Nothing was changed.");
        if (request.IdempotencyKey is not null && request.Open is null)
            throw WorkException.Invalid("idempotency_key goes with open; resolve is guarded by expected_version. Nothing was changed.");

        if (request.Open is { } open)
        {
            var rule = await RequireEntryAsync(open.Id, "open.id", null, session, ct);
            if (rule.Kind != KnowledgeKind.Lesson)
                throw WorkException.Invalid($"Entry {rule.Id} is a {WorkJson.Name(rule.Kind)}; challenge disputes rules (lessons that are rules). Nothing was changed.");
            if (open.Evidence.Count is 0 or > LessonLimits.MaxEvidence)
                throw WorkException.Invalid($"open.evidence must hold 1 to {LessonLimits.MaxEvidence} entries: a challenge presents evidence (a test result, a source, a counter-example, a link). Nothing was changed.");
            var evidence = open.Evidence.Select((item, index) => new EvidenceView(
                item.Kind,
                Summary(Text(item.Summary, $"open.evidence[{index}].summary", LessonLimits.MaxEvidenceSummaryLength), $"open.evidence[{index}].summary"),
                Optional(item.Ref, $"open.evidence[{index}].ref", KnowledgeLimits.MaxSourceRefLength))).ToList();
            return await Knowledge.OpenChallengeAsync(rule.Id, Text(open.Reason, "open.reason", KnowledgeLimits.MaxReasonLength), evidence,
                request.IdempotencyKey is { } key ? HashKey(actor, key) : null, actor, ct);
        }

        var resolve = request.Resolve!;
        if (!Guid.TryParse(resolve.ChallengeId.Trim(), out var challengeId))
            throw WorkException.Invalid("resolve.challenge_id must be the id of a challenge.");
        var challenge = await Knowledge.GetChallengeAsync(challengeId, ct)
            ?? throw WorkException.NotFound($"There is no challenge {challengeId}.");
        var current = await RequireEntryAsync(challenge.EntryId.ToString(), "resolve.challenge_id", null, session, ct);
        var reason = Text(resolve.Reason, "resolve.reason", KnowledgeLimits.MaxReasonLength);

        KnowledgeSpec? amended = null;
        if (resolve.Outcome == ChallengeOutcome.Amend)
        {
            if (resolve.Amend is not { } amend || (amend.Summary is null && amend.What is null && amend.AppliesWhen is null))
                throw WorkException.Invalid("outcome amend needs amend with summary, what or applies_when: the rule as amended. Nothing was changed.");
            amended = ReadRevision(current, new KnowledgeRevisionInput
            {
                Id = current.Id.ToString(), ExpectedVersion = resolve.ExpectedVersion,
                Summary = amend.Summary, What = amend.What, AppliesWhen = amend.AppliesWhen
            });
        }
        else if (resolve.Amend is not null)
        {
            throw WorkException.Invalid($"amend goes with outcome amend, not {WorkJson.Name(resolve.Outcome)}. Nothing was changed.");
        }

        var result = await Knowledge.ResolveChallengeAsync(challengeId, resolve.ExpectedVersion, resolve.Outcome, reason, amended, actor, ct);
        return result;
    }

    // ---- lessons and review_queue (data for the Lessons page and the review queue) ------------------

    private async Task<object> LessonsAsync(LessonsRequest request, SessionView? session, CancellationToken ct)
    {
        var project = await KnowledgeProjectAsync(request.Project, session, ct);
        var limit = request.Limit ?? LessonLimits.DefaultLimit;
        if (limit is < 1 or > LessonLimits.MaxLimit)
            throw WorkException.Invalid($"limit must be 1 to {LessonLimits.MaxLimit}.");
        if (request.SinceDays is not null && request.View != LessonsView.Unmatched)
            throw WorkException.Invalid("since_days goes with view unmatched.");
        var since = request.SinceDays ?? LessonLimits.DefaultUnmatchedDays;
        if (since is < 1 or > 3650)
            throw WorkException.Invalid("since_days must be 1 to 3650.");
        var offset = request.Cursor is { } cursor ? DecodeOffset(cursor, LessonsCursorKind, "lessons") : 0;
        if (request.View == LessonsView.UnlearnedReviews && request.Activity is not null)
            throw WorkException.Invalid("activity does not go with view unlearned_reviews: a review comment has no applies-when (source review or bot_review narrows it).");
        if (request.View == LessonsView.UnlearnedReviews && request.Source is not (null or CorrectionSourceKind.Review or CorrectionSourceKind.BotReview))
            throw WorkException.Invalid("With view unlearned_reviews, source is review (human reviews) or bot_review.");

        var page = await Knowledge.ReadLessonsAsync(new LessonsSpec(project.Id, request.View, request.Activity, request.Source, since, offset, limit), ct);
        var next = page.More ? EncodeCursor(LessonsCursorKind, (offset + limit).ToString(CultureInfo.InvariantCulture)) : null;
        if (request.View == LessonsView.UnlearnedReviews)
            return new LessonsResult(request.View, NextCursor: next, Reviews: page.Reviews ?? []);
        if (request.View != LessonsView.Unaddressed)
            return new LessonsResult(request.View, Items: page.Items, NextCursor: next);

        // Grouped by activity (a lesson with two activities is in both groups), as input for a skill update.
        var groups = page.Items
            .SelectMany(row => (row.Activity ?? []).Where(activity => request.Activity is null || activity == request.Activity).Select(activity => (activity, row)))
            .GroupBy(pair => pair.activity)
            .OrderBy(group => group.Key)
            .Select(group => new LessonGroup(group.Key, group.Select(pair => pair.row).ToList()))
            .ToList();
        return new LessonsResult(request.View, Groups: groups, NextCursor: next);
    }

    private async Task<object> ReviewQueueAsync(ReviewQueueRequest request, SessionView? session, CancellationToken ct)
    {
        var project = await KnowledgeProjectAsync(request.Project, session, ct);
        var limit = request.Limit ?? LessonLimits.DefaultLimit;
        if (limit is < 1 or > LessonLimits.MaxLimit)
            throw WorkException.Invalid($"limit must be 1 to {LessonLimits.MaxLimit}.");
        var offset = request.Cursor is { } cursor ? DecodeOffset(cursor, ReviewCursorKind, "review_queue") : 0;
        var page = await Knowledge.ReadReviewQueueAsync(project.Id, offset, limit, ct);
        return new ReviewQueueResult(page.Items, page.More ? EncodeCursor(ReviewCursorKind, (offset + limit).ToString(CultureInfo.InvariantCulture)) : null);
    }

    private static int DecodeOffset(string cursor, string kind, string tool)
    {
        var value = DecodeCursor(cursor, kind, tool);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset
            : throw WorkException.Invalid($"cursor is not a cursor of {tool}; pass next_cursor back unchanged.");
    }
}
