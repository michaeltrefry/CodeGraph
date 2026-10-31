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
        int Count(string name) => digest.Data.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;
        var reads = new List<object>();
        if (Count("stale_count") > 0)
            reads.Add(new { tool = "dream_read", arguments = (object)new { possibly_stale = true, limit = 5 }, purpose = "Select relevant stale claims, then read each selected id in full and check its owning source." });
        else if (Count("changed_count") > 0 || Count("knowledge_count") > 0)
            reads.Add(new { tool = "dream_read", arguments = (object)new { limit = 5 }, purpose = "Use changed_entries in signals first; otherwise select relevant claims from this compact index. Read a selected id in full before judging it." });
        if (Count("open_challenge_count") > 0 || Count("open_proposal_count") > 0)
            reads.Add(new { tool = "dream_review_queue", arguments = (object)new { }, purpose = "Inspect existing open disputes/proposals before acting on their targets; leave review decisions to their owners." });
        if (Count("lesson_event_count") > 0)
            reads.Add(new { tool = "dream_lessons", arguments = (object)new { view = "recurrence" }, purpose = "Inspect recurrence only when relevant to selected work; other lesson views remain available through the tool schema." });
        if (Count("work_trail_count") > 0)
            reads.Add(new { tool = "dream_work_trail", arguments = (object)new { since, detail = "summary", limit = 5 }, purpose = "Newest trail metadata and short previews for selecting evidence. Use each row's read arguments for exact full content; summaries cannot justify a judgment." });
        // The first turn is an index, never a body/history dump. Each section is read lazily
        // through bounded run-scoped pages, so growth cannot prevent the model from starting.
        var data = JsonSerializer.SerializeToElement(new
        {
            untrusted_data = true, signals = digest.Data,
            reads,
            selection = "These are signal-based suggestions, not a checklist. Select useful evidence-backed work early, batch independent reads, and report when the signals do not justify more work. All tool capabilities remain available, including complete history by detail full without since.",
            pagination = "Every read returns total_count, has_more and next_cursor. Pass next_cursor unchanged as cursor to the same tool. Pages are immutable within this run. Oversized items use exact JSON fragments; concatenate content until has_more is false, then parse JSON. Source reads remain separate and are required for verify."
        }, WorkJson.Options);
        return new(data, digest.Fingerprint, digest.HasSignal);
    }

    public async Task<object> ExecuteToolAsync(string tool, JsonElement args, WorkActor actor, CancellationToken ct = default)
    {
        var scope = DreamPolicy.RequireScope(actor);
        DreamPolicy.RequireProject(actor, scope.ProjectId);
        var validated = WorkJson.ReadObject(System.Text.Encoding.UTF8.GetBytes(args.GetRawText()),tool);
        var input = JsonNode.Parse(validated.GetRawText())!.AsObject();
        if (input.ContainsKey("project") || input.ContainsKey("project_id") || input.ContainsKey("session_id") || input.ContainsKey("run_id") || input.ContainsKey("actor"))
            throw new WorkException(WorkErrorCodes.Forbidden,"Dream tools use the server run's project and actor; they cannot be overridden. Nothing was changed.");
        if (tool == "dream_propose")
        {
            input["project"] = scope.ProjectId.ToString();
            input["run_id"] = scope.RunId.ToString();
            var request = WorkJson.Parse<DreamProposalRequest>(JsonSerializer.SerializeToElement(input), tool);
            await RequireEntryAsync(request.Target, "target", scope.ProjectId.ToString(), null, ct);
            if (request.Other is { } other) await RequireEntryAsync(other, "other", scope.ProjectId.ToString(), null, ct);
            return new { untrusted_data = true, data = await DreamProposeAsync(request, actor, null, ct) };
        }
        if (tool is "dream_read" or "dream_lessons" or "dream_similar" or "dream_review_queue" or "dream_work_trail")
            return new { untrusted_data = true, data = await ReadBoundedDreamToolAsync(tool, input, scope, ct) };
        var envelope = WorkJson.Parse<DreamWriteEvidence>(JsonSerializer.SerializeToElement(new { evidence = input["evidence"], prevalence = input["prevalence"] }), tool);
        var evidence = envelope.Evidence;
        var prevalence = envelope.Prevalence;
        if (evidence.Count is 0 or > 10 || string.IsNullOrWhiteSpace(prevalence) || prevalence.Length > 1000)
            throw WorkException.Invalid("Every dream write requires 1 to 10 evidence references and prevalence (1 to 1000 characters).");
        await DreamKnowledge.ValidateDreamEvidenceAsync(scope.ProjectId, evidence, ct);
        using var evidenceScope = scope.UseEvidence(evidence);
        using var metadata = DreamWriteMetadata.Begin(prevalence);
        input.Remove("evidence"); input.Remove("prevalence");
        var priorJournal = await DreamKnowledge.LastDreamJournalIdAsync(scope.ProjectId, scope.RunId, ct);
        object result;
        switch (tool)
        {
            case "dream_verify":
            {
                var request = WorkJson.Parse<KnowledgeVerifyInput>(JsonSerializer.SerializeToElement(input), tool);
                var entry = await RequireEntryAsync(request.Id, "id", scope.ProjectId.ToString(), null, ct);
                DreamPolicy.RequireWrite(actor, "verify");
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
                    throw new WorkException(WorkErrorCodes.Forbidden, "Dreaming revises applies_when only on non-prompt lessons; open a challenge for a prompt rule.");
                var state = ReadRevision(entry, request);
                state = state with
                {
                    Repositories = request.AppliesWhen?.Repositories is not null ? state.Details!.AppliesWhen!.Repositories ?? [] : state.Repositories,
                    Paths = request.AppliesWhen?.Paths is not null ? state.Details!.AppliesWhen!.Paths ?? [] : state.Paths
                };
                result = new RecordResult(false, await Knowledge.ReviseKnowledgeAsync(entry.Id, request.ExpectedVersion, state, actor, ct));
                break;
            }
            case "dream_relate":
            {
                var request = WorkJson.Parse<KnowledgeRelateInput>(JsonSerializer.SerializeToElement(input), tool);
                var entry = await RequireEntryAsync(request.Id, "id", scope.ProjectId.ToString(), null, ct);
                var otherId = request.SameAs ?? request.Contradicts ?? throw WorkException.Invalid("relate needs same_as or contradicts.");
                var other = await RequireEntryAsync(otherId, "other", scope.ProjectId.ToString(), null, ct);
                if (request.SameAs is not null && (entry.Details?.Source?.Kind == CorrectionSourceKind.Prompt || other.Details?.Source?.Kind == CorrectionSourceKind.Prompt))
                    throw new WorkException(WorkErrorCodes.Forbidden, "A merge involving a prompt lesson requires a proposal.");
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
                if (input["entry"] is null) throw WorkException.Invalid("dream_record requires entry.");
                var entry = WorkJson.Parse<KnowledgeEntryInput>(JsonSerializer.SerializeToElement(input["entry"]), tool);
                if (entry.Confidence is null || entry.Kind is not (KnowledgeKind.Fact or KnowledgeKind.Gotcha or KnowledgeKind.Constraint or KnowledgeKind.Attempt or KnowledgeKind.Lesson))
                    throw new WorkException(WorkErrorCodes.Forbidden, "Dreaming records fact, gotcha, constraint, attempt or lesson with explicit confidence.");
                if (entry.Supersedes is not null || entry.SameAs is not null || entry.Contradicts is not null)
                    throw new WorkException(WorkErrorCodes.Forbidden, "Record a new entry independently; use dream_relate for relations and a proposal for supersession.");
                if (entry.Kind == KnowledgeKind.Lesson && entry.Source?.Kind != CorrectionSourceKind.Agent)
                    throw new WorkException(WorkErrorCodes.Forbidden, "A dreaming lesson must have source agent.");
                result = await RecordAsync(new() { Project = scope.ProjectId.ToString(), Entry = entry }, actor, null, ct);
                break;
            }
            case "dream_challenge":
            {
                RefuseDreamFields(input, "id", "reason");
                var request = WorkJson.Parse<DreamModelContract.ChallengeInput>(JsonSerializer.SerializeToElement(input), tool);
                var entry = await RequireEntryAsync(request.Id, "id", scope.ProjectId.ToString(), null, ct);
                var reason = Text(request.Reason, "reason", KnowledgeLimits.MaxReasonLength);
                result = await Knowledge.OpenChallengeAsync(entry.Id, reason, evidence.Select(e => new EvidenceView(EvidenceKind.Source, e.Summary, e.Reference)).ToList(), null, actor, ct);
                break;
            }
            default:
                throw new WorkException(WorkErrorCodes.Forbidden, $"Dreaming cannot execute '{tool}'. Only reversible knowledge changes and proposals are allowed.");
        }
        var journal = await DreamKnowledge.LastDreamJournalIdAsync(scope.ProjectId, scope.RunId, ct);
        return new { untrusted_data = true, data = result, evidence, prevalence, journal_id = journal != priorJournal ? journal : null };
    }

    private sealed class DreamWriteEvidence
    {
        public required List<DreamEvidence> Evidence { get; init; }
        public required string Prevalence { get; init; }
    }

    private static void RefuseDreamFields(JsonObject input, params string[] allowed)
    {
        var unknown = input.Select(pair => pair.Key).Except(allowed, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw WorkException.Invalid("Unsupported dream arguments: " + string.Join(", ", unknown) + ". Valid arguments: " + string.Join(", ", allowed) + ".");
    }

    private async Task<object> ReadDreamToolPageAsync(string tool, JsonObject input, Guid projectId, CancellationToken ct)
    {
        if (tool == "dream_read")
        {
            if (input["id"] is { } id)
            {
                RefuseDreamFields(input, "id", "detail");
                var request = WorkJson.Parse<RecallRequest>(JsonSerializer.SerializeToElement(input), tool);
                var entry = await RequireEntryAsync(id.GetValue<string>(), "id", projectId.ToString(), null, ct);
                return (await Knowledge.GetKnowledgeAsync(entry.Id, withRevisions: request.Detail is null or WorkDetail.Full, ct))!;
            }
            input["project"] = projectId.ToString();
            return await RecallAsync(WorkJson.Parse<RecallRequest>(JsonSerializer.SerializeToElement(input), tool), null, ct);
        }
        if (tool == "dream_lessons")
        {
            input["project"] = projectId.ToString();
            return await LessonsAsync(WorkJson.Parse<LessonsRequest>(JsonSerializer.SerializeToElement(input), tool), null, ct, completeDreamSnapshot: true);
        }
        if (tool == "dream_review_queue")
        {
            input["project"] = projectId.ToString();
            return await ReviewQueueAsync(WorkJson.Parse<ReviewQueueRequest>(JsonSerializer.SerializeToElement(input), tool), null, ct, completeDreamSnapshot: true);
        }
        if (tool == "dream_similar") RefuseDreamFields(input, "offset", "limit");
        else RefuseDreamFields(input, "offset", "limit", "since");
        var offset = input["offset"]?.GetValue<int>() ?? 0;
        var limit = input["limit"]?.GetValue<int>() ?? 50;
        if (offset < 0 || limit is < 1 or > 100) throw WorkException.Invalid("offset must be nonnegative; limit must be 1 to 100.");
        if (tool == "dream_similar") return await DreamKnowledge.ReadDreamSimilarAsync(projectId, offset, int.MaxValue - 1, ct);
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
