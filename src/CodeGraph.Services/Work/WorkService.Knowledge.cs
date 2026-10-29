using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

// The knowledge tools (S5): record, recall, import_knowledge, the save-point fields of update_work
// and end_session, and the knowledge sections of the briefing and the item brief. Arguments are read
// strictly here, as for every work tool; the store checks scope against the database. No model call
// is made anywhere on this path (E5): dedup is an exact match of normalized summaries.
public sealed partial class WorkService
{
    private const string RecallCursorKind = "kn";

    // The kinds a briefing serves: decisions and rules. Attempts only when linked to the session's
    // items or scoped to its paths (the store's rule).
    private static readonly KnowledgeKind[] BriefingKinds =
        [KnowledgeKind.Decision, KnowledgeKind.Constraint, KnowledgeKind.Lesson, KnowledgeKind.Gotcha, KnowledgeKind.Attempt];

    private static readonly KnowledgeKind[] BriefKinds =
        [KnowledgeKind.Decision, KnowledgeKind.Constraint, KnowledgeKind.Lesson, KnowledgeKind.Gotcha, KnowledgeKind.Fact, KnowledgeKind.Attempt];

    // ---- Save points ---------------------------------------------------------------------------

    /// <summary>The save-point fields of a write as the entries it stores (empty when none is passed).</summary>
    private static IReadOnlyList<KnowledgeSpec> ReadSavePoints(
        IReadOnlyList<DecisionInput>? decisions, IReadOnlyList<AttemptInput>? attempts,
        IReadOnlyList<CorrectionInput>? corrections, IReadOnlyList<FindingInput>? findings, Guid? linkedItem)
    {
        var specs = new List<KnowledgeSpec>();
        foreach (var (decision, index) in Indexed(decisions, "decisions"))
        {
            var where = $"decisions[{index}]";
            var (repositories, paths, nodes, commit) = ReadScope(decision.Repositories, decision.Paths, decision.Nodes, decision.Commit, where);
            specs.Add(new KnowledgeSpec(
                KnowledgeKind.Decision, Summary(decision.Decision, $"{where}.decision"), null, decision.Confidence ?? KnowledgeConfidence.Medium,
                decision.MadeAutonomously,
                Details(new KnowledgeDetails(
                    Why: Field(decision.Why, $"{where}.why"),
                    Alternatives: ReadAlternatives(decision.Alternatives, $"{where}.alternatives"),
                    Context: Field(decision.Context, $"{where}.context"),
                    Consequences: Field(decision.Consequences, $"{where}.consequences"))),
                repositories, paths, nodes, commit, linkedItem));
        }

        foreach (var (attempt, index) in Indexed(attempts, "attempts"))
        {
            var where = $"attempts[{index}]";
            var (repositories, paths, nodes, commit) = ReadScope(attempt.Repositories, attempt.Paths, attempt.Nodes, attempt.Commit, where);
            specs.Add(new KnowledgeSpec(
                KnowledgeKind.Attempt, Summary(attempt.Tried, $"{where}.tried"), null, attempt.Confidence ?? KnowledgeConfidence.Medium, null,
                new KnowledgeDetails(WhyFailed: RequiredField(attempt.WhyFailed, $"{where}.why_failed")),
                repositories, paths, nodes, commit, linkedItem));
        }

        foreach (var (correction, index) in Indexed(corrections, "corrections"))
        {
            var where = $"corrections[{index}]";
            var appliesWhen = ReadAppliesWhen(correction.AppliesWhen, $"{where}.applies_when");
            specs.Add(new KnowledgeSpec(
                KnowledgeKind.Lesson, Summary(correction.CorrectBehavior, $"{where}.correct_behavior"), null, correction.Confidence ?? KnowledgeConfidence.High, null,
                new KnowledgeDetails(
                    What: RequiredField(correction.What, $"{where}.what"),
                    AppliesWhen: appliesWhen,
                    Source: ReadSource(correction.Source, $"{where}.source")),
                appliesWhen?.Repositories, appliesWhen?.Paths ?? [], [], null, linkedItem));
        }

        foreach (var (finding, index) in Indexed(findings, "findings"))
        {
            var where = $"findings[{index}]";
            var (repositories, paths, nodes, commit) = ReadScope(finding.Repositories, finding.Paths, finding.Nodes, finding.Commit, where);
            specs.Add(new KnowledgeSpec(
                finding.Kind switch { FindingKind.Fact => KnowledgeKind.Fact, FindingKind.Gotcha => KnowledgeKind.Gotcha, _ => KnowledgeKind.Constraint },
                Summary(finding.Summary, $"{where}.summary"), Body(finding.Body, $"{where}.body"), finding.Confidence ?? KnowledgeConfidence.Medium, null, null,
                repositories, paths, nodes, commit, linkedItem));
        }

        return specs;

        static IEnumerable<(T Value, int Index)> Indexed<T>(IReadOnlyList<T>? values, string argument)
        {
            if (values is null)
                return [];
            if (values.Count > KnowledgeLimits.MaxSaveEntries)
                throw WorkException.Invalid($"{argument} holds at most {KnowledgeLimits.MaxSaveEntries} entries. Nothing was changed.");
            return values.Select((value, index) => (value, index));
        }
    }

    /// <summary>The rows of what a write's save points stored, or null when it stored none.</summary>
    private async Task<IReadOnlyList<KnowledgeRow>?> RecordedAsync(IReadOnlyList<KnowledgeSpec> specs, CancellationToken ct)
    {
        if (specs.Count == 0)
            return null;
        var rows = await Knowledge.GetKnowledgeRowsAsync(specs.Select(spec => spec.Id).ToList(), ct);
        return rows.Count == 0 ? null : rows;
    }

    // ---- Briefing and brief -------------------------------------------------------------------------

    private async Task<IReadOnlyList<KnowledgeRow>?> BriefingKnowledgeAsync(Guid sessionId, BriefingView briefing, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var session = await Store.GetSessionAsync(sessionId, ct);
        if (session is null)
            return null;

        var items = new List<Guid>();
        foreach (var key in (briefing.MyWork ?? []).Select(item => item.Key).Append(briefing.Focus?.Key).OfType<string>())
        {
            if (await Store.ResolveAsync(key, ct) is { Kind: WorkEntityKind.Item } target)
                items.Add(target.Id);
        }

        var rows = await Knowledge.ReadRelevantKnowledgeAsync(
            new KnowledgeContext(session.ProjectId, session.Repository, paths, items, BriefingKinds, KnowledgeLimits.MaxBriefingEntries), ct);
        return rows.Count == 0 ? null : rows;
    }

    // The knowledge of an item brief: linked to the item, scoped to its repository, or project-wide.
    // Seam for S4: once items carry touches, pass them as the context's paths.
    private async Task<IReadOnlyList<KnowledgeRow>?> ItemKnowledgeAsync(ItemView item, CancellationToken ct)
    {
        var rows = await Knowledge.ReadRelevantKnowledgeAsync(
            new KnowledgeContext(item.ProjectId, item.Repository, [], [item.Id], BriefKinds, KnowledgeLimits.MaxBriefingEntries), ct);
        return rows.Count == 0 ? null : rows;
    }

    // ---- record ------------------------------------------------------------------------------------

    private async Task<object> RecordAsync(RecordRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        var chosen = new[]
        {
            ("entry", request.Entry is not null), ("revise", request.Revise is not null),
            ("retract", request.Retract is not null), ("verify", request.Verify is not null)
        }.Where(entry => entry.Item2).Select(entry => entry.Item1).ToList();
        if (chosen.Count != 1)
            throw WorkException.Invalid($"record takes exactly one of entry, revise, retract, verify; this call passes {(chosen.Count == 0 ? "none" : string.Join(" and ", chosen))}. Nothing was changed.");
        if (request.IdempotencyKey is not null && request.Entry is null)
            throw WorkException.Invalid("idempotency_key goes with entry (a new entry); revise, retract and verify are guarded by expected_version. Nothing was changed.");

        if (request.Entry is { } entry)
        {
            var project = await KnowledgeProjectAsync(request.Project, session, ct);
            var spec = await ReadEntryAsync(entry, project, "entry", allowLinks: true, ct);
            return await Knowledge.RecordKnowledgeAsync(project.Id, spec, actor, request.IdempotencyKey is { } key ? HashKey(actor, key) : null, ct);
        }

        var (id, expectedVersion) = request.Revise is { } revise ? (revise.Id, revise.ExpectedVersion)
            : request.Retract is { } retract ? (retract.Id, retract.ExpectedVersion)
            : (request.Verify!.Id, request.Verify.ExpectedVersion);
        var where = chosen[0];
        var current = await RequireEntryAsync(id, $"{where}.id", request.Project, session, ct);

        if (request.Revise is { } revision)
            return new RecordResult(false, await Knowledge.ReviseKnowledgeAsync(current.Id, expectedVersion, ReadRevision(current, revision), actor, ct));
        if (request.Retract is { } retraction)
            return new RecordResult(false, await Knowledge.RetractKnowledgeAsync(current.Id, expectedVersion,
                Text(retraction.Reason, "retract.reason", KnowledgeLimits.MaxReasonLength), actor, ct));
        return new RecordResult(false, await Knowledge.VerifyKnowledgeAsync(current.Id, expectedVersion,
            Optional(request.Verify!.Commit, "verify.commit", WorkSessionLimits.MaxCommitLength), actor, ct));
    }

    // The project of a knowledge call: named, or the session's.
    private async Task<WorkTarget> KnowledgeProjectAsync(string? project, SessionView? session, CancellationToken ct)
    {
        if (project is null && session is not null)
            return new WorkTarget(WorkEntityKind.Project, session.ProjectId, session.ProjectId);
        var target = await RequireProjectAsync(project, "project", ct);
        await RequireSessionProjectAsync(session, target.Id, ct);
        return target;
    }

    private async Task<KnowledgeView> RequireEntryAsync(string id, string argument, string? project, SessionView? session, CancellationToken ct)
    {
        if (!Guid.TryParse(id.Trim(), out var entryId))
            throw WorkException.Invalid($"{argument} must be the id of a knowledge entry.");
        var entry = await Knowledge.GetKnowledgeAsync(entryId, withRevisions: false, ct)
            ?? throw WorkException.NotFound($"There is no knowledge entry {entryId}.");
        if (project is not null && (await RequireProjectAsync(project, "project", ct)).Id != entry.ProjectId)
            throw WorkException.Invalid($"Entry {entryId} belongs to project {entry.ProjectKey}, not {project}. Nothing was changed.");
        await RequireSessionProjectAsync(session, entry.ProjectId, ct);
        return entry;
    }

    // Which of the kind-specific fields each kind takes.
    private static readonly Dictionary<KnowledgeKind, string[]> KindFields = new()
    {
        [KnowledgeKind.Decision] = ["why", "alternatives", "context", "consequences", "made_autonomously"],
        [KnowledgeKind.Attempt] = ["why_failed"],
        [KnowledgeKind.Lesson] = ["what", "applies_when", "source"]
    };

    private static readonly string[] AllKindFields = ["why", "alternatives", "context", "consequences", "made_autonomously", "why_failed", "what", "applies_when", "source"];

    private static void RefuseForeignKindFields(KnowledgeKind kind, IEnumerable<string> passed, string where)
    {
        var valid = KindFields.GetValueOrDefault(kind) ?? [];
        var foreign = passed.Where(name => AllKindFields.Contains(name) && !valid.Contains(name)).ToList();
        if (foreign.Count > 0)
            throw WorkException.Invalid(
                $"{where}: a {WorkJson.Name(kind)} entry takes no {string.Join(", ", foreign)}" +
                (valid.Length == 0 ? " (those are fields of decisions, attempts and lessons)." : $"; its own fields are {string.Join(", ", valid)}.") + " Nothing was changed.");
    }

    private static IEnumerable<string> PassedNames(object input) =>
        JsonSerializer.SerializeToNode(input, input.GetType(), WorkJson.Options)!.AsObject().Select(property => property.Key);

    private async Task<KnowledgeSpec> ReadEntryAsync(KnowledgeEntryInput input, WorkTarget project, string where, bool allowLinks, CancellationToken ct)
    {
        RefuseForeignKindFields(input.Kind, PassedNames(input), where);
        if (!allowLinks && (input.Item is not null || input.Supersedes is not null))
            throw WorkException.Invalid($"{where} takes no item and no supersedes here. Nothing was changed.");
        if (input.Kind == KnowledgeKind.Attempt && input.WhyFailed is null)
            throw WorkException.Invalid($"{where}: an attempt entry needs why_failed. Nothing was changed.");

        var (repositories, paths, nodes, commit) = ReadScope(input.Repositories, input.Paths, input.Nodes, input.Commit, where);
        var appliesWhen = ReadAppliesWhen(input.AppliesWhen, $"{where}.applies_when");
        if (appliesWhen is not null)
        {
            // A lesson's location is its scope.
            repositories = (repositories ?? []).Concat(appliesWhen.Repositories ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            paths = paths.Concat(appliesWhen.Paths ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (repositories.Count == 0)
                repositories = null;
        }

        Guid? itemId = input.Item is { } item ? (await RequireOfProjectAsync(item, WorkEntityKind.Item, project, $"{where}.item", ct)).Id : null;
        SupersedeSpec? supersedes = null;
        if (input.Supersedes is { } replaced)
        {
            if (!Guid.TryParse(replaced.Id.Trim(), out var replacedId))
                throw WorkException.Invalid($"{where}.supersedes.id must be the id of a knowledge entry.");
            supersedes = new SupersedeSpec(replacedId, replaced.ExpectedVersion);
        }

        return new KnowledgeSpec(
            input.Kind, Summary(input.Summary, $"{where}.summary"), Body(input.Body, $"{where}.body"), input.Confidence ?? KnowledgeConfidence.Medium,
            input.Kind == KnowledgeKind.Decision ? input.MadeAutonomously : null,
            Details(new KnowledgeDetails(
                Why: Field(input.Why, $"{where}.why"),
                Alternatives: ReadAlternatives(input.Alternatives, $"{where}.alternatives"),
                Context: Field(input.Context, $"{where}.context"),
                Consequences: Field(input.Consequences, $"{where}.consequences"),
                WhyFailed: Field(input.WhyFailed, $"{where}.why_failed"),
                What: Field(input.What, $"{where}.what"),
                AppliesWhen: appliesWhen,
                Source: input.Source is { } source ? ReadSource(source, $"{where}.source") : null)),
            repositories, paths, nodes, commit, itemId, Supersedes: supersedes);
    }

    private static readonly string[] RevisionClearable = ["body", "why", "alternatives", "context", "consequences", "what", "applies_when", "repositories", "paths", "nodes"];

    // The entry as the revision leaves it: the current content with the fields passed replaced.
    private static KnowledgeSpec ReadRevision(KnowledgeView current, KnowledgeRevisionInput input)
    {
        var passed = PassedNames(input).Where(name => name is not ("id" or "expected_version")).ToList();
        if (passed.Count == 0)
            throw WorkException.Invalid("revise passes nothing to change. Nothing was changed.");
        RefuseForeignKindFields(current.Kind, passed, "revise");
        var clear = (input.Clear ?? []).Select(name => name.Trim()).ToList();
        var unknown = clear.Where(name => !RevisionClearable.Contains(name)).ToList();
        if (unknown.Count > 0)
            throw WorkException.Invalid($"revise.clear can name {string.Join(", ", RevisionClearable)}; not {string.Join(", ", unknown)}. Nothing was changed.");
        var both = clear.Where(passed.Contains).ToList();
        if (both.Count > 0)
            throw WorkException.Invalid($"{string.Join(", ", both)} is both passed and named in revise.clear. Nothing was changed.");

        var (repositories, paths, nodes, commit) = ReadScope(input.Repositories, input.Paths, input.Nodes, input.Commit, "revise");
        var details = current.Details ?? new KnowledgeDetails();
        details = details with
        {
            Why = clear.Contains("why") ? null : Field(input.Why, "revise.why") ?? details.Why,
            Alternatives = clear.Contains("alternatives") ? null : ReadAlternatives(input.Alternatives, "revise.alternatives") ?? details.Alternatives,
            Context = clear.Contains("context") ? null : Field(input.Context, "revise.context") ?? details.Context,
            Consequences = clear.Contains("consequences") ? null : Field(input.Consequences, "revise.consequences") ?? details.Consequences,
            WhyFailed = Field(input.WhyFailed, "revise.why_failed") ?? details.WhyFailed,
            What = clear.Contains("what") ? null : Field(input.What, "revise.what") ?? details.What,
            AppliesWhen = clear.Contains("applies_when") ? null : ReadAppliesWhen(input.AppliesWhen, "revise.applies_when") ?? details.AppliesWhen,
            Source = input.Source is { } source ? ReadSource(source, "revise.source") : details.Source
        };

        return new KnowledgeSpec(
            current.Kind,
            input.Summary is { } summary ? Summary(summary, "revise.summary") : current.Summary,
            clear.Contains("body") ? null : Body(input.Body, "revise.body") ?? current.Body,
            input.Confidence ?? current.Confidence,
            current.Kind == KnowledgeKind.Decision ? input.MadeAutonomously ?? current.MadeAutonomously : null,
            Details(details),
            clear.Contains("repositories") ? [] : repositories ?? current.Repositories ?? [],
            clear.Contains("paths") ? [] : input.Paths is not null ? paths : current.Paths ?? [],
            clear.Contains("nodes") ? [] : input.Nodes is not null ? nodes : current.Nodes?.Select(node => node.Id).ToList() ?? [],
            commit);
    }

    // ---- recall ------------------------------------------------------------------------------------

    private async Task<object> RecallAsync(RecallRequest request, SessionView? session, CancellationToken ct)
    {
        var detail = request.Detail ?? WorkDetail.Summary;
        if (request.Id is { } id)
        {
            var filters = PassedNames(request).Where(name => name is not ("id" or "session_id" or "project" or "detail")).ToList();
            if (filters.Count > 0)
                throw WorkException.Invalid($"recall with id takes no {string.Join(", ", filters)}: it answers that one entry.");
            if (!Guid.TryParse(id.Trim(), out var entryId))
                throw WorkException.Invalid("id must be the id of a knowledge entry.");
            var entry = await Knowledge.GetKnowledgeAsync(entryId, withRevisions: detail == WorkDetail.Full, ct)
                ?? throw WorkException.NotFound($"There is no knowledge entry {entryId}.");
            if (request.Project is { } named && (await RequireProjectAsync(named, "project", ct)).Id != entry.ProjectId)
                throw WorkException.NotFound($"Project {named} has no knowledge entry {entryId}.");
            await RequireSessionProjectAsync(session, entry.ProjectId, ct);
            return new RecallResult([entry]);
        }

        var project = await KnowledgeProjectAsync(request.Project, session, ct);
        var maxLimit = detail == WorkDetail.Full ? KnowledgeLimits.MaxFullRecallLimit : KnowledgeLimits.MaxRecallLimit;
        var limit = request.Limit ?? Math.Min(KnowledgeLimits.DefaultRecallLimit, maxLimit);
        if (limit < 1 || limit > maxLimit)
            throw WorkException.Invalid($"limit must be 1 to {maxLimit}{(detail == WorkDetail.Full ? " with detail full" : string.Empty)}.");

        var words = (request.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > KnowledgeLimits.MaxRecallWords)
            throw WorkException.Invalid($"text holds at most {KnowledgeLimits.MaxRecallWords} words.");
        var nodes = request.Nodes ?? [];
        if (nodes.Count > KnowledgeLimits.MaxNodes)
            throw WorkException.Invalid($"nodes holds at most {KnowledgeLimits.MaxNodes} node ids.");

        Guid? itemId = request.Item is { } item ? (await RequireOfProjectAsync(item, WorkEntityKind.Item, project, "item", ct)).Id : null;
        var page = await Knowledge.RecallAsync(new RecallSpec(
            project.Id,
            request.Kind,
            OptionalName(request.Repository, "repository"),
            ReadPaths(request.Paths, "paths"),
            nodes,
            itemId,
            request.Activity,
            request.Source,
            words,
            request.Status is { Count: > 0 } statuses ? statuses : [KnowledgeStatus.Active],
            request.PossiblyStale,
            detail,
            limit,
            request.Cursor is { } cursor ? DecodeRecallCursor(cursor) : null), ct);

        return new RecallResult(page.Items, page.Next is { } next
            ? EncodeCursor(RecallCursorKind, $"{next.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture)}.{next.Id}")
            : null);
    }

    private static KnowledgeCursor DecodeRecallCursor(string cursor)
    {
        var value = DecodeCursor(cursor, RecallCursorKind, "recall");
        var parts = value.Split('.');
        if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParse(parts[1], out var id))
            return new KnowledgeCursor(new DateTime(ticks, DateTimeKind.Utc), id);
        throw WorkException.Invalid("cursor is not a cursor of recall; pass next_cursor back unchanged.");
    }

    // ---- import_knowledge ------------------------------------------------------------------------------

    private async Task<object> ImportKnowledgeAsync(ImportKnowledgeRequest request, WorkActor actor, SessionView? session, CancellationToken ct)
    {
        var project = await KnowledgeProjectAsync(request.Project, session, ct);
        if (request.Candidates.Count is 0 or > KnowledgeLimits.MaxImportCandidates)
            throw WorkException.Invalid($"candidates must hold 1 to {KnowledgeLimits.MaxImportCandidates} candidates; it holds {request.Candidates.Count}.");

        var confirm = request.Confirm ?? false;
        if (!confirm && (request.Accept is not null || request.PreviewToken is not null))
            throw WorkException.Invalid("accept and preview_token go with confirm true; a dry run (confirm false) takes neither and stores nothing.");
        if (confirm && (request.Accept is not { Count: > 0 } || request.PreviewToken is null))
            throw WorkException.Invalid("confirm true needs accept (the indexes of the candidates to store) and the preview_token of the dry run of this batch. Nothing was stored.");

        var token = PreviewToken(project.Id, request.Candidates);
        if (confirm && !string.Equals(request.PreviewToken!.Trim(), token, StringComparison.Ordinal))
            throw new WorkException(WorkErrorCodes.Conflict,
                "preview_token is not the token of a dry run of this batch: the candidates differ from the ones previewed. Run the dry run again and confirm its token. Nothing was stored.");

        var candidates = new List<ImportCandidateSpec>();
        foreach (var (candidate, index) in request.Candidates.Select((candidate, index) => (candidate, index)))
        {
            try
            {
                var spec = await ReadEntryAsync(candidate.Entry, project, $"candidates[{index}].entry", allowLinks: false, ct);
                var sourceFile = Text(candidate.SourceFile, $"candidates[{index}].source_file", KnowledgeLimits.MaxSourceFileLength);
                _ = Optional(candidate.VerificationNote, $"candidates[{index}].verification_note", KnowledgeLimits.MaxFieldLength);
                candidates.Add(new ImportCandidateSpec(index, spec with { SourceFile = sourceFile }, candidate.Verification));
            }
            catch (WorkException exception) when (exception.Code == WorkErrorCodes.InvalidArgument)
            {
                candidates.Add(new ImportCandidateSpec(index, null, candidate.Verification, exception.Message.Replace(" Nothing was changed.", "", StringComparison.Ordinal)));
            }
        }

        return await Knowledge.ImportKnowledgeAsync(project.Id, candidates, token, confirm ? request.Accept : null, actor, ct);
    }

    // Binds a confirmation to the batch that was previewed (stateless: nothing is stored by a dry run).
    private static string PreviewToken(Guid projectId, IReadOnlyList<ImportCandidateInput> candidates) =>
        "kp_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            projectId.ToString() + "\n" + JsonSerializer.Serialize(candidates, WorkJson.Options))))[..32];

    // ---- Field rules -------------------------------------------------------------------------------

    private static string Summary(string value, string argument)
    {
        var text = Text(value, argument, KnowledgeLimits.MaxSummaryLength);
        if (text.Contains('\n') || text.Contains('\r'))
            throw WorkException.Invalid($"{argument} must be one line (put more in body or the kind's fields). Nothing was changed.");
        return text;
    }

    private static string? Body(string? value, string argument) => Optional(value, argument, KnowledgeLimits.MaxBodyLength);

    private static string? Field(string? value, string argument) => Optional(value, argument, KnowledgeLimits.MaxFieldLength);

    private static string RequiredField(string value, string argument) => Text(value, argument, KnowledgeLimits.MaxFieldLength);

    private static KnowledgeDetails? Details(KnowledgeDetails details) => details == new KnowledgeDetails() ? null : details;

    private static IReadOnlyList<string>? ReadAlternatives(IReadOnlyList<string>? values, string argument)
    {
        if (values is null)
            return null;
        if (values.Count > KnowledgeLimits.MaxAlternatives)
            throw WorkException.Invalid($"{argument} holds at most {KnowledgeLimits.MaxAlternatives} entries.");
        return values.Select((value, index) => Text(value, $"{argument}[{index}]", KnowledgeLimits.MaxFieldLength)).ToList();
    }

    private static CorrectionSourceView ReadSource(CorrectionSourceInput source, string argument) =>
        new(source.Kind,
            source.Kind switch { CorrectionSourceKind.Prompt => "highest", CorrectionSourceKind.Review => "high", _ => "normal" },
            Optional(source.Ref, $"{argument}.ref", KnowledgeLimits.MaxSourceRefLength));

    private static AppliesWhenView? ReadAppliesWhen(AppliesWhenInput? input, string argument)
    {
        if (input is null)
            return null;
        var repositories = ReadRepositoryNames(input.Repositories, $"{argument}.repositories");
        var paths = ReadPaths(input.Paths, $"{argument}.paths");
        var tools = input.Tools?.Select((tool, index) => Text(tool, $"{argument}.tools[{index}]", KnowledgeLimits.MaxToolNameLength)).Distinct(StringComparer.Ordinal).ToList();
        var view = new AppliesWhenView(
            input.Activity is { Count: > 0 } activity ? activity.Distinct().ToList() : null,
            repositories is { Count: > 0 } ? repositories : null,
            paths.Count > 0 ? paths : null,
            tools is { Count: > 0 } ? tools : null,
            Optional(input.Harness, $"{argument}.harness", WorkSessionLimits.MaxHarnessLength));
        return view == new AppliesWhenView() ? null : view;
    }

    private static (IReadOnlyList<string>? Repositories, IReadOnlyList<string> Paths, IReadOnlyList<long> Nodes, string? Commit) ReadScope(
        IReadOnlyList<string>? repositories, IReadOnlyList<string>? paths, IReadOnlyList<long>? nodes, string? commit, string where)
    {
        if (nodes is { Count: > KnowledgeLimits.MaxNodes })
            throw WorkException.Invalid($"{where}.nodes holds at most {KnowledgeLimits.MaxNodes} node ids.");
        return (ReadRepositoryNames(repositories, $"{where}.repositories"), ReadPaths(paths, $"{where}.paths"),
            nodes?.Distinct().ToList() ?? [], Optional(commit, $"{where}.commit", WorkSessionLimits.MaxCommitLength));
    }

    private static IReadOnlyList<string>? ReadRepositoryNames(IReadOnlyList<string>? repositories, string argument)
    {
        if (repositories is null)
            return null;
        if (repositories.Count > KnowledgeLimits.MaxRepositories)
            throw WorkException.Invalid($"{argument} holds at most {KnowledgeLimits.MaxRepositories} repositories.");
        return repositories.Select((repository, index) => OptionalName(repository, $"{argument}[{index}]")!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Paths in their stored form: relative, forward slashes, at most 50 of at most 500 characters.</summary>
    private static IReadOnlyList<string> ReadPaths(IReadOnlyList<string>? paths, string argument)
    {
        if (paths is null)
            return [];
        if (paths.Count > KnowledgeLimits.MaxPaths)
            throw WorkException.Invalid($"{argument} holds at most {KnowledgeLimits.MaxPaths} paths.");
        var normalized = new List<string>();
        foreach (var (path, index) in paths.Select((path, index) => (path, index)))
        {
            var value = KnowledgePaths.Normalize(path);
            if (value is null || value.Length > KnowledgeLimits.MaxPathLength)
                throw WorkException.Invalid($"{argument}[{index}] '{path}' must be a path relative to the repository root (no '..', no drive), at most {KnowledgeLimits.MaxPathLength} characters. Nothing was changed.");
            if (!normalized.Contains(value, StringComparer.Ordinal))
                normalized.Add(value);
        }

        return normalized;
    }
}
