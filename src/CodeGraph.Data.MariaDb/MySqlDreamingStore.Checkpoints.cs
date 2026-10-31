using System.Text.Json;
using CodeGraph.Models.Work;
using Dapper;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlDreamingStore
{
    private sealed record CheckpointRow(Guid RunId, string StateJson);

    public async Task<DreamCheckpoint?> GetCheckpointAsync(Guid projectId, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<CheckpointRow>(new CommandDefinition(
            "SELECT run_id AS RunId,state_json AS StateJson FROM dreaming_checkpoints WHERE project_id=@projectId",
            new { projectId }, cancellationToken: ct));
        return row is null ? null : new(projectId, row.RunId, JsonSerializer.Deserialize<JsonElement>(row.StateJson));
    }

    public async Task SaveProgressAsync(DreamRun run, JsonElement? checkpoint, Guid? expectedCheckpointRunId, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(run.ProjectId);
        if (run.Id == Guid.Empty || run.ProjectId == Guid.Empty || checkpoint is { ValueKind: JsonValueKind.Undefined })
            throw WorkException.Invalid("Run and project ids and checkpoint JSON must be valid.");
        run=run with {ContinuationAvailable=checkpoint is not null};
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // Lock a stable parent even before the first checkpoint exists. An absent-row read alone
        // cannot serialize compare-and-set writers under all supported isolation levels.
        if (await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM work_projects WHERE id=@ProjectId FOR UPDATE", new { run.ProjectId }, tx, cancellationToken: ct)) is null)
            throw WorkException.NotFound("The checkpoint project does not exist.");
        var existingProject = await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT project_id FROM dreaming_runs WHERE id=@Id FOR UPDATE", new { run.Id }, tx, cancellationToken: ct));
        if (existingProject is { } owner && owner != run.ProjectId)
            throw new WorkException(WorkErrorCodes.Conflict, "The run belongs to another project. Nothing was saved.");
        var current = await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT run_id FROM dreaming_checkpoints WHERE project_id=@ProjectId FOR UPDATE", new { run.ProjectId }, tx, cancellationToken: ct));
        if (current != expectedCheckpointRunId)
            throw new WorkException(WorkErrorCodes.Conflict, "The dreaming checkpoint owner changed. Nothing was saved.");
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO dreaming_runs(id,project_id,status,started_at,ended_at,run_json) VALUES(@Id,@ProjectId,@Status,@StartedAt,@EndedAt,@json) ON DUPLICATE KEY UPDATE status=VALUES(status),ended_at=VALUES(ended_at),run_json=VALUES(run_json)",
            new { run.Id, run.ProjectId, run.Status, run.StartedAt, run.EndedAt, json = JsonSerializer.Serialize(run, WorkJson.Options) }, tx, cancellationToken: ct));
        if (checkpoint is { } state)
            await c.ExecuteAsync(new CommandDefinition(
                "INSERT INTO dreaming_checkpoints(project_id,run_id,state_json,updated_at) VALUES(@ProjectId,@Id,@json,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE run_id=VALUES(run_id),state_json=VALUES(state_json),updated_at=VALUES(updated_at)",
                new { run.ProjectId, run.Id, json = state.GetRawText() }, tx, cancellationToken: ct));
        else
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM dreaming_checkpoints WHERE project_id=@ProjectId", new { run.ProjectId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private sealed class ExecutionJournalRow
    {
        public Guid RunId { get; init; }
        public long Id { get; init; }
        public Guid EntryId { get; init; }
        public string Action { get; init; } = "";
        public string AfterJson { get; init; } = "";
        public string EvidenceJson { get; init; } = "[]";
        public string Prevalence { get; init; } = "";
        public DateTime? RevertedAt { get; init; }
    }
    private sealed class ExecutionProposalRow
    {
        public Guid RunId { get; init; }
        public Guid Id { get; init; }
        public Guid TargetId { get; init; }
        public string Action { get; init; } = "";
        public string Status { get; init; } = "";
        public string Rationale { get; init; } = "";
        public string Prevalence { get; init; } = "";
        public string EvidenceJson { get; init; } = "[]";
        public string? Draft { get; init; }
    }

    public async Task<DreamExecutionOutcome?> GetExecutionResultAsync(Guid projectId, Guid executionId, CancellationToken ct = default)
    {
        DreamPolicy.RequireCurrentProject(projectId);
        await using var c = await OpenAsync(ct);
        var write = await c.QuerySingleOrDefaultAsync<ExecutionJournalRow>(new CommandDefinition(
            "SELECT run_id AS RunId,id,entry_id AS EntryId,action,after_json AS AfterJson,evidence_json AS EvidenceJson,prevalence,reverted_at AS RevertedAt FROM dream_write_journal WHERE project_id=@projectId AND execution_id=@executionId",
            new { projectId, executionId }, cancellationToken: ct));
        if (write is not null)
            return new(write.RunId, JsonSerializer.SerializeToElement(new
            {
                untrusted_data = true, recovered_from_journal = true, journal_id = write.Id, run_id = write.RunId,
                execution_id = executionId, evidence = JsonSerializer.Deserialize<JsonElement>(write.EvidenceJson), prevalence = write.Prevalence,
                data = new { entry_id = write.EntryId, action = write.Action, after = JsonSerializer.Deserialize<JsonElement>(write.AfterJson), reverted_at = write.RevertedAt }
            }, WorkJson.Options));
        var proposal = await c.QuerySingleOrDefaultAsync<ExecutionProposalRow>(new CommandDefinition(
            "SELECT run_id AS RunId,id,target_id AS TargetId,action,status,rationale,prevalence,evidence_json AS EvidenceJson,draft FROM dream_proposals WHERE project_id=@projectId AND execution_id=@executionId",
            new { projectId, executionId }, cancellationToken: ct));
        return proposal is null ? null : new(proposal.RunId, JsonSerializer.SerializeToElement(new
        {
            untrusted_data = true, recovered_from_proposal = true, run_id = proposal.RunId, execution_id = executionId,
            data = new { created = true, suppressed = false, proposal = new { id = proposal.Id, run_id = proposal.RunId, target = proposal.TargetId,
                action = proposal.Action, status = proposal.Status, rationale = proposal.Rationale, prevalence = proposal.Prevalence,
                evidence = JsonSerializer.Deserialize<JsonElement>(proposal.EvidenceJson), draft = proposal.Draft } }
        }, WorkJson.Options));
    }
}
