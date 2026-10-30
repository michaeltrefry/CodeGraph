using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

public sealed partial class WorkService : IKnowledgeDreamActions
{
    private IDreamingKnowledgeStore DreamKnowledge => Store as IDreamingKnowledgeStore ?? throw WorkException.Invalid("Dreaming requires a reversible knowledge store.");

    public async Task<DreamDigest> BuildDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default)
    {
        var digest = await DreamKnowledge.ReadDreamDigestAsync(projectId, since, ct);
        var views = new Dictionary<string, LessonsPage>();
        foreach (var view in new[] { LessonsView.Recurrence, LessonsView.Unaddressed, LessonsView.Unmatched, LessonsView.UnlearnedReviews })
            views[WorkJson.Name(view)] = await Knowledge.ReadLessonsAsync(new(projectId, view, null, null, LessonLimits.DefaultUnmatchedDays, 0, 50), ct);
        var data = JsonSerializer.SerializeToElement(new
        {
            untrusted_data = true, signals = digest.Data, lessons = views,
            similar_pairs = await DreamKnowledge.ReadDreamSimilarAsync(projectId, 0, 50, ct),
            review_queue = await Knowledge.ReadReviewQueueAsync(projectId, 0, 50, ct),
            work_trail = await DreamKnowledge.ReadDreamWorkTrailAsync(projectId, since, 0, 50, ct),
            pagination = "All sections expose more flags; use dream_read, dream_lessons, dream_similar, dream_review_queue and dream_work_trail to continue."
        }, WorkJson.Options);
        return new(data, digest.Fingerprint, digest.HasSignal);
    }

    public async Task<object> ExecuteToolAsync(string tool, JsonElement args, WorkActor actor, CancellationToken ct = default)
    {
        var scope = DreamExecutionScope.Current ?? throw WorkException.Invalid("Dream tools require an active run.");
        DreamPolicy.RequireProject(actor, scope.ProjectId);
        var input = JsonNode.Parse(args.GetRawText()) as JsonObject ?? throw WorkException.Invalid("Tool arguments must be an object.");
        if (input.ContainsKey("project") || input.ContainsKey("project_id") || input.ContainsKey("session_id"))
            throw WorkException.Invalid("Dream tools use the run's project and actor; they cannot be overridden.");
        if (tool is "dream_read" or "dream_lessons" or "dream_similar" or "dream_review_queue" or "dream_work_trail")
            return new { untrusted_data = true, data = await ReadDreamToolAsync(tool, input, scope.ProjectId, ct) };
        var evidence = input["evidence"]?.Deserialize<List<DreamEvidence>>(WorkJson.Options) ?? throw WorkException.Invalid("Every dream write requires evidence.");
        var prevalence = input["prevalence"]?.GetValue<string>();
        if (evidence.Count is 0 or > 10 || string.IsNullOrWhiteSpace(prevalence) || prevalence.Length > 1000)
            throw WorkException.Invalid("Every dream write requires 1 to 10 evidence references and prevalence (1 to 1000 characters).");
        await DreamKnowledge.ValidateDreamEvidenceAsync(scope.ProjectId, evidence, ct);
        using var evidenceScope = scope.UseEvidence(evidence);
        using var metadata = DreamWriteMetadata.Begin(prevalence);
        input.Remove("evidence"); input.Remove("prevalence");
        object result;
        switch (tool)
        {
            case "dream_verify":
            {
                var request = WorkJson.Parse<KnowledgeVerifyInput>(JsonSerializer.SerializeToElement(input), tool);
                var entry = await RequireEntryAsync(request.Id, "id", scope.ProjectId.ToString(), null, ct);
                DreamPolicy.RequireVerification(actor, entry);
                result = await RecordAsync(new() { Verify = request }, actor, null, ct);
                break;
            }
            case "dream_revise":
            {
                RefuseDreamFields(input, "id", "expected_version", "applies_when");
                if (input["applies_when"] is null) throw WorkException.Invalid("dream_revise requires applies_when.");
                var request = WorkJson.Parse<KnowledgeRevisionInput>(JsonSerializer.SerializeToElement(input), tool);
                var entry = await RequireEntryAsync(request.Id, "id", scope.ProjectId.ToString(), null, ct);
                if (entry.Kind != KnowledgeKind.Lesson || entry.Details?.Source?.Kind == CorrectionSourceKind.Prompt)
                    throw WorkException.Invalid("Dreaming revises applies_when only on non-prompt lessons; open a challenge for a prompt rule.");
                result = await RecordAsync(new() { Revise = request }, actor, null, ct);
                break;
            }
            case "dream_relate":
            {
                var request = WorkJson.Parse<KnowledgeRelateInput>(JsonSerializer.SerializeToElement(input), tool);
                var entry = await RequireEntryAsync(request.Id, "id", scope.ProjectId.ToString(), null, ct);
                var otherId = request.SameAs ?? request.Contradicts ?? throw WorkException.Invalid("relate needs same_as or contradicts.");
                var other = await RequireEntryAsync(otherId, "other", scope.ProjectId.ToString(), null, ct);
                if (request.SameAs is not null && (entry.Details?.Source?.Kind == CorrectionSourceKind.Prompt || other.Details?.Source?.Kind == CorrectionSourceKind.Prompt))
                    throw WorkException.Invalid("A merge involving a prompt lesson requires a proposal.");
                result = await RecordAsync(new() { Relate = request }, actor, null, ct);
                break;
            }
            case "dream_promote":
            {
                RefuseDreamFields(input, "id", "expected_version");
                input["action"] = "propose";
                var request = WorkJson.Parse<PromoteRequest>(JsonSerializer.SerializeToElement(input), tool);
                await RequireEntryAsync(request.Id, "id", scope.ProjectId.ToString(), null, ct);
                result = await PromoteAsync(request, actor, null, ct);
                break;
            }
            case "dream_record":
            {
                RefuseDreamFields(input, "entry");
                var entry = input["entry"]?.Deserialize<KnowledgeEntryInput>(WorkJson.Options) ?? throw WorkException.Invalid("dream_record requires entry.");
                if (entry.Confidence is null || entry.Kind is not (KnowledgeKind.Fact or KnowledgeKind.Gotcha or KnowledgeKind.Constraint or KnowledgeKind.Attempt or KnowledgeKind.Lesson))
                    throw WorkException.Invalid("Dreaming records fact, gotcha, constraint, attempt or lesson with explicit confidence.");
                if (entry.Supersedes is not null || entry.SameAs is not null || entry.Contradicts is not null)
                    throw WorkException.Invalid("Record a new entry independently; use dream_relate for relations and a proposal for supersession.");
                if (entry.Kind == KnowledgeKind.Lesson && entry.Source?.Kind != CorrectionSourceKind.Agent)
                    throw WorkException.Invalid("A dreaming lesson must have source agent.");
                result = await RecordAsync(new() { Project = scope.ProjectId.ToString(), Entry = entry }, actor, null, ct);
                break;
            }
            case "dream_challenge":
            {
                RefuseDreamFields(input, "id", "reason");
                var id = input["id"]?.GetValue<string>() ?? throw WorkException.Invalid("challenge requires id.");
                var entry = await RequireEntryAsync(id, "id", scope.ProjectId.ToString(), null, ct);
                var reason = Text(input["reason"]?.GetValue<string>() ?? throw WorkException.Invalid("challenge requires reason."), "reason", KnowledgeLimits.MaxReasonLength);
                result = await Knowledge.OpenChallengeAsync(entry.Id, reason, evidence.Select(e => new EvidenceView(EvidenceKind.Source, e.Summary, e.Reference)).ToList(), null, actor, ct);
                break;
            }
            default:
                throw WorkException.Invalid($"Dreaming cannot execute '{tool}'. Only reversible knowledge changes and proposals are allowed.");
        }
        return new { untrusted_data = true, data = result, evidence, prevalence };
    }

    private static void RefuseDreamFields(JsonObject input, params string[] allowed)
    {
        var unknown = input.Select(pair => pair.Key).Except(allowed, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw WorkException.Invalid("Unsupported dream arguments: " + string.Join(", ", unknown));
    }

    private async Task<object> ReadDreamToolAsync(string tool, JsonObject input, Guid projectId, CancellationToken ct)
    {
        if (tool == "dream_read")
        {
            if (input["id"] is { } id)
            {
                RefuseDreamFields(input, "id");
                return await RequireEntryAsync(id.GetValue<string>(), "id", projectId.ToString(), null, ct);
            }
            input["project"] = projectId.ToString();
            return await RecallAsync(WorkJson.Parse<RecallRequest>(JsonSerializer.SerializeToElement(input), tool), null, ct);
        }
        if (tool == "dream_lessons")
        {
            input["project"] = projectId.ToString();
            return await LessonsAsync(WorkJson.Parse<LessonsRequest>(JsonSerializer.SerializeToElement(input), tool), null, ct);
        }
        if (tool == "dream_review_queue")
        {
            input["project"] = projectId.ToString();
            return await ReviewQueueAsync(WorkJson.Parse<ReviewQueueRequest>(JsonSerializer.SerializeToElement(input), tool), null, ct);
        }
        RefuseDreamFields(input, "offset", "limit", "since");
        var offset = input["offset"]?.GetValue<int>() ?? 0;
        var limit = input["limit"]?.GetValue<int>() ?? 50;
        if (offset < 0 || limit is < 1 or > 100) throw WorkException.Invalid("offset must be nonnegative; limit must be 1 to 100.");
        if (tool == "dream_similar") return await DreamKnowledge.ReadDreamSimilarAsync(projectId, offset, limit, ct);
        var since = input["since"]?.GetValue<DateTime?>();
        return await DreamKnowledge.ReadDreamWorkTrailAsync(projectId, since, offset, limit, ct);
    }

    public async Task<DreamRevertResult> RevertAsync(Guid projectId, Guid runId, WorkActor actor, CancellationToken ct = default)
    {
        if (DreamExecutionScope.Current is not null || actor.Kind == WorkActor.SystemKind && actor.Name == "dreamer")
            throw WorkException.Invalid("The dreamer cannot revert runs.");
        var entries = await DreamKnowledge.RevertDreamWritesAsync(projectId, runId, actor, ct);
        return new(runId, entries.Select(entry => new DreamRevertEntry(entry.JournalId, entry.EntryId, entry.Action, entry.Status, entry.Reason)).ToList());
    }
}
