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
        // The first turn is an index, never a body/history dump. Each section is read lazily
        // through bounded run-scoped pages, so growth cannot prevent the model from starting.
        var data = JsonSerializer.SerializeToElement(new
        {
            untrusted_data = true, signals = digest.Data,
            reads = new object[]
            {
                new { tool = "dream_read", arguments = new { status = new[] { "active", "superseded", "retracted" } }, purpose = "All knowledge, including revised, superseded and retracted entries; read id for details." },
                new { tool = "dream_read", arguments = new { possibly_stale = true }, purpose = "All currently possibly_stale entries." },
                new { tool = "dream_lessons", arguments = new { view = "recurrence" }, purpose = "Recurrence with and without rule served." },
                new { tool = "dream_lessons", arguments = new { view = "unaddressed" }, purpose = "Rules not addressed by skill, hook or test." },
                new { tool = "dream_lessons", arguments = new { view = "unmatched" }, purpose = "Rules not served recently." },
                new { tool = "dream_lessons", arguments = new { view = "unlearned_reviews" }, purpose = "Review evidence without a lesson; full review text is in work trail." },
                new { tool = "dream_similar", arguments = new { }, purpose = "Unrelated similar lesson pairs across the complete project." },
                new { tool = "dream_review_queue", arguments = new { }, purpose = "All open review items." },
                new { tool = "dream_work_trail", arguments = new { since }, purpose = "Changed revision snapshots plus attempts, handoffs, threads, messages and GitHub review evidence since the watermark. Omit since for complete history." }
            },
            pagination = "Every read returns total_count, has_more and next_cursor. Pass next_cursor unchanged as cursor to the same tool. Pages are immutable within this run. Oversized items use exact JSON fragments; concatenate content until has_more is false, then parse JSON. Source reads remain separate and are required for verify."
        }, WorkJson.Options);
        return new(data, digest.Fingerprint, digest.HasSignal);
    }

    public async Task<object> ExecuteToolAsync(string tool, JsonElement args, WorkActor actor, CancellationToken ct = default)
    {
        var scope = DreamPolicy.RequireScope(actor);
        DreamPolicy.RequireProject(actor, scope.ProjectId);
        var input = JsonNode.Parse(args.GetRawText()) as JsonObject ?? throw WorkException.Invalid("Tool arguments must be an object.");
        if (input.ContainsKey("project") || input.ContainsKey("project_id") || input.ContainsKey("session_id"))
            throw WorkException.Invalid("Dream tools use the run's project and actor; they cannot be overridden.");
        if (tool == "dream_propose")
        {
            input["project"] = scope.ProjectId.ToString();
            input["run_id"] = scope.RunId.ToString();
            return new { untrusted_data = true, data = await DreamProposeAsync(WorkJson.Parse<DreamProposalRequest>(JsonSerializer.SerializeToElement(input), tool), actor, null, ct) };
        }
        if (tool is "dream_read" or "dream_lessons" or "dream_similar" or "dream_review_queue" or "dream_work_trail")
            return new { untrusted_data = true, data = await ReadBoundedDreamToolAsync(tool, input, scope, ct) };
        var evidence = input["evidence"]?.Deserialize<List<DreamEvidence>>(WorkJson.Options) ?? throw WorkException.Invalid("Every dream write requires evidence.");
        var prevalence = input["prevalence"]?.GetValue<string>();
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
                var entry = input["entry"]?.Deserialize<KnowledgeEntryInput>(WorkJson.Options) ?? throw WorkException.Invalid("dream_record requires entry.");
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
                var id = input["id"]?.GetValue<string>() ?? throw WorkException.Invalid("challenge requires id.");
                var entry = await RequireEntryAsync(id, "id", scope.ProjectId.ToString(), null, ct);
                var reason = Text(input["reason"]?.GetValue<string>() ?? throw WorkException.Invalid("challenge requires reason."), "reason", KnowledgeLimits.MaxReasonLength);
                result = await Knowledge.OpenChallengeAsync(entry.Id, reason, evidence.Select(e => new EvidenceView(EvidenceKind.Source, e.Summary, e.Reference)).ToList(), null, actor, ct);
                break;
            }
            default:
                throw new WorkException(WorkErrorCodes.Forbidden, $"Dreaming cannot execute '{tool}'. Only reversible knowledge changes and proposals are allowed.");
        }
        var journal = await DreamKnowledge.LastDreamJournalIdAsync(scope.ProjectId, scope.RunId, ct);
        return new { untrusted_data = true, data = result, evidence, prevalence, journal_id = journal != priorJournal ? journal : null };
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
        RefuseDreamFields(input, "offset", "limit", "since");
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
