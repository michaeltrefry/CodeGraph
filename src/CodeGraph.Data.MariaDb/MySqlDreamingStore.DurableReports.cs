using System.Text.Json;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlDreamingStore
{
    private sealed class DurableWriteRow
    {
        public long Id { get; init; }
        public Guid EntryId { get; init; }
        public string Action { get; init; } = "";
        public string BeforeJson { get; init; } = "";
        public string AfterJson { get; init; } = "";
        public string EvidenceJson { get; init; } = "[]";
        public string Prevalence { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime? RevertedAt { get; init; }
    }
    private sealed class DurableProposalRow
    {
        public Guid Id { get; init; }
        public Guid TargetId { get; init; }
        public long TargetVersion { get; init; }
        public string Action { get; init; } = "";
        public string Rationale { get; init; } = "";
        public string Prevalence { get; init; } = "";
        public string EvidenceJson { get; init; } = "[]";
        public string Status { get; init; } = "";
        public string? Draft { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private static string DurableTool(string action) => action switch
    {
        "record" => "dream_record", "verified" => "dream_verify", "revised" => "dream_revise",
        "same_as" or "contradicts" => "dream_relate", "propose" => "dream_promote", "challenge" => "dream_challenge",
        _ => "dream_" + action
    };

    private static bool HasJournal(DreamAction action, long id) => action.Result.ValueKind == JsonValueKind.Object &&
        action.Result.TryGetProperty("journal_id", out var stored) && stored.ValueKind == JsonValueKind.Number && stored.GetInt64() == id;

    private static bool HasProposal(DreamAction action, Guid id)
    {
        if (action.Result.ValueKind != JsonValueKind.Object) return false;
        var result = action.Result.TryGetProperty("data", out var data) ? data : action.Result;
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("proposal", out var proposal) &&
            proposal.ValueKind == JsonValueKind.Object && proposal.TryGetProperty("id", out var stored) && stored.TryGetGuid(out var actual) && actual == id;
    }

    // Committed mutations are authoritative even if the process died before SaveRunAsync recorded
    // the tool response. Read/error/no-op actions stay intact; durable ids prevent duplicates.
    private static async Task<DreamRun> RecoverDurableActionsAsync(MySqlConnection connection, DreamRun run, CancellationToken ct)
    {
        var actions = run.Actions.ToList();
        var writes = await connection.QueryAsync<DurableWriteRow>(new CommandDefinition(
            "SELECT id,entry_id AS EntryId,action,before_json AS BeforeJson,after_json AS AfterJson,evidence_json AS EvidenceJson,prevalence,created_at AS CreatedAt,reverted_at AS RevertedAt FROM dream_write_journal WHERE project_id=@ProjectId AND run_id=@Id ORDER BY id",
            new { run.ProjectId, run.Id }, cancellationToken: ct));
        foreach (var write in writes)
        {
            if (actions.Any(action => HasJournal(action, write.Id))) continue;
            var evidence = JsonSerializer.Deserialize<List<DreamEvidence>>(write.EvidenceJson, WorkJson.Options) ?? [];
            var arguments = JsonSerializer.SerializeToElement(new { id = write.EntryId, action = write.Action, evidence, prevalence = write.Prevalence }, WorkJson.Options);
            var result = JsonSerializer.SerializeToElement(new
            {
                untrusted_data = true, journal_id = write.Id, recovered_from_journal = true, evidence, prevalence = write.Prevalence,
                data = new { entry_id = write.EntryId, action = write.Action, before = JsonSerializer.Deserialize<JsonElement>(write.BeforeJson), after = JsonSerializer.Deserialize<JsonElement>(write.AfterJson), reverted_at = write.RevertedAt }
            }, WorkJson.Options);
            actions.Add(new(DurableTool(write.Action), arguments, result, evidence, DateTime.SpecifyKind(write.CreatedAt, DateTimeKind.Utc)));
        }
        var proposals = await connection.QueryAsync<DurableProposalRow>(new CommandDefinition(
            "SELECT id,target_id AS TargetId,target_version AS TargetVersion,action,rationale,prevalence,evidence_json AS EvidenceJson,status,draft,created_at AS CreatedAt FROM dream_proposals WHERE project_id=@ProjectId AND run_id=@Id ORDER BY created_at,id",
            new { run.ProjectId, run.Id }, cancellationToken: ct));
        foreach (var proposal in proposals)
        {
            if (actions.Any(action => HasProposal(action, proposal.Id))) continue;
            var storedEvidence = JsonSerializer.Deserialize<List<DreamEvidenceView>>(proposal.EvidenceJson, WorkJson.Options) ?? [];
            var evidence = storedEvidence.Select(item => new DreamEvidence(WorkJson.Name(item.Kind), item.Id, "Durable proposal evidence; fingerprint " + item.Fingerprint)).ToList();
            var arguments = JsonSerializer.SerializeToElement(new { action = proposal.Action, target = proposal.TargetId, expected_version = proposal.TargetVersion, rationale = proposal.Rationale, prevalence = proposal.Prevalence, evidence = storedEvidence, draft = proposal.Draft }, WorkJson.Options);
            var result = JsonSerializer.SerializeToElement(new { untrusted_data = true, recovered_from_proposal = true, data = new { proposal = new { id = proposal.Id, target = proposal.TargetId, action = proposal.Action, status = proposal.Status, rationale = proposal.Rationale, prevalence = proposal.Prevalence, evidence = storedEvidence, draft = proposal.Draft } } }, WorkJson.Options);
            actions.Add(new("dream_propose", arguments, result, evidence, DateTime.SpecifyKind(proposal.CreatedAt, DateTimeKind.Utc)));
        }
        return run with { Actions = actions.OrderBy(action => action.CreatedAt).ToList() };
    }
}
