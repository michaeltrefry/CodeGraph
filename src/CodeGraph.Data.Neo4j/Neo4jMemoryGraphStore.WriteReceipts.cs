using Neo4j.Driver;
using CodeGraph.Models.Memory;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jMemoryGraphStore
{
    public async Task CreateWriteReceiptAsync(MemoryWriteReceipt receipt)
    {
        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MERGE (r:MemoryWriteReceipt {id: $id})
                SET r.source = $source,
                    r.inputMode = $inputMode,
                    r.status = $status,
                    r.entitiesRequested = $entitiesRequested,
                    r.claimsRequested = $claimsRequested,
                    r.evidenceRequested = $evidenceRequested,
                    r.attemptCount = $attemptCount,
                    r.nodesWritten = $nodesWritten,
                    r.edgesWritten = $edgesWritten,
                    r.conflictsDetected = $conflictsDetected,
                    r.claimsWritten = $claimsWritten,
                    r.evidenceWritten = $evidenceWritten,
                    r.observationsWritten = $observationsWritten,
                    r.errorMessage = $errorMessage,
                    r.createdAt = datetime($createdAt),
                    r.updatedAt = datetime($updatedAt),
                    r.startedAt = CASE WHEN $startedAt IS NULL THEN null ELSE datetime($startedAt) END,
                    r.completedAt = CASE WHEN $completedAt IS NULL THEN null ELSE datetime($completedAt) END
                """,
                new
                {
                    id = receipt.Id,
                    source = receipt.Source,
                    inputMode = receipt.InputMode,
                    status = ToWriteReceiptStatus(receipt.Status),
                    entitiesRequested = receipt.EntitiesRequested,
                    claimsRequested = receipt.ClaimsRequested,
                    evidenceRequested = receipt.EvidenceRequested,
                    attemptCount = receipt.AttemptCount,
                    nodesWritten = receipt.NodesWritten,
                    edgesWritten = receipt.EdgesWritten,
                    conflictsDetected = receipt.ConflictsDetected,
                    claimsWritten = receipt.ClaimsWritten,
                    evidenceWritten = receipt.EvidenceWritten,
                    observationsWritten = receipt.ObservationsWritten,
                    errorMessage = receipt.ErrorMessage,
                    createdAt = receipt.CreatedAt.ToString("O"),
                    updatedAt = receipt.UpdatedAt.ToString("O"),
                    startedAt = receipt.StartedAt?.ToString("O"),
                    completedAt = receipt.CompletedAt?.ToString("O"),
                });
        });
    }

    public async Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var result = await session.RunAsync(
            """
            MATCH (r:MemoryWriteReceipt {id: $receiptId})
            RETURN r.id AS id, r.source AS source, r.inputMode AS inputMode,
                   r.status AS status,
                   coalesce(r.entitiesRequested, 0) AS entitiesRequested,
                   coalesce(r.claimsRequested, 0) AS claimsRequested,
                   coalesce(r.evidenceRequested, 0) AS evidenceRequested,
                   coalesce(r.attemptCount, 0) AS attemptCount,
                   coalesce(r.nodesWritten, 0) AS nodesWritten,
                   coalesce(r.edgesWritten, 0) AS edgesWritten,
                   coalesce(r.conflictsDetected, 0) AS conflictsDetected,
                   coalesce(r.claimsWritten, 0) AS claimsWritten,
                   coalesce(r.evidenceWritten, 0) AS evidenceWritten,
                   coalesce(r.observationsWritten, 0) AS observationsWritten,
                   r.errorMessage AS errorMessage,
                   r.createdAt AS createdAt,
                   r.updatedAt AS updatedAt,
                   r.startedAt AS startedAt,
                   r.completedAt AS completedAt
            LIMIT 1
            """,
            new { receiptId });

        if (!await result.FetchAsync())
            return null;

        var record = result.Current;
        return new MemoryWriteReceipt
        {
            Id = record["id"].As<string>(),
            Source = record["source"].As<string?>() ?? "api",
            InputMode = record["inputMode"].As<string?>() ?? "typed",
            Status = ParseWriteReceiptStatus(record["status"].As<string?>()),
            EntitiesRequested = record["entitiesRequested"].As<int>(),
            ClaimsRequested = record["claimsRequested"].As<int>(),
            EvidenceRequested = record["evidenceRequested"].As<int>(),
            AttemptCount = record["attemptCount"].As<int>(),
            NodesWritten = record["nodesWritten"].As<int>(),
            EdgesWritten = record["edgesWritten"].As<int>(),
            ConflictsDetected = record["conflictsDetected"].As<int>(),
            ClaimsWritten = record["claimsWritten"].As<int>(),
            EvidenceWritten = record["evidenceWritten"].As<int>(),
            ObservationsWritten = record["observationsWritten"].As<int>(),
            ErrorMessage = record["errorMessage"].As<string?>(),
            CreatedAt = TryReadDateTime(record, "createdAt") ?? DateTime.UtcNow,
            UpdatedAt = TryReadDateTime(record, "updatedAt") ?? DateTime.UtcNow,
            StartedAt = TryReadDateTime(record, "startedAt"),
            CompletedAt = TryReadDateTime(record, "completedAt"),
        };
    }

    public async Task UpdateWriteReceiptStatusAsync(string receiptId, MemoryWriteReceiptStatus status, StoreMemoryResult? result = null,
        string? errorMessage = null)
    {
        var now = DateTime.UtcNow;

        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MATCH (r:MemoryWriteReceipt {id: $receiptId})
                SET r.status = $status,
                    r.updatedAt = datetime($updatedAt),
                    r.errorMessage = $errorMessage,
                    r.startedAt = CASE
                        WHEN $status = 'processing' THEN coalesce(r.startedAt, datetime($updatedAt))
                        ELSE r.startedAt
                    END,
                    r.completedAt = CASE
                        WHEN $status IN ['completed', 'failed'] THEN datetime($updatedAt)
                        ELSE r.completedAt
                    END,
                    r.attemptCount = CASE
                        WHEN $status = 'processing' THEN coalesce(r.attemptCount, 0) + 1
                        ELSE coalesce(r.attemptCount, 0)
                    END,
                    r.nodesWritten = coalesce($nodesWritten, r.nodesWritten),
                    r.edgesWritten = coalesce($edgesWritten, r.edgesWritten),
                    r.conflictsDetected = coalesce($conflictsDetected, r.conflictsDetected),
                    r.claimsWritten = coalesce($claimsWritten, r.claimsWritten),
                    r.evidenceWritten = coalesce($evidenceWritten, r.evidenceWritten),
                    r.observationsWritten = coalesce($observationsWritten, r.observationsWritten)
                """,
                new
                {
                    receiptId,
                    status = ToWriteReceiptStatus(status),
                    updatedAt = now.ToString("O"),
                    errorMessage,
                    nodesWritten = result?.NodesWritten,
                    edgesWritten = result?.EdgesWritten,
                    conflictsDetected = result?.ConflictsDetected,
                    claimsWritten = result?.ClaimsWritten,
                    evidenceWritten = result?.EvidenceWritten,
                    observationsWritten = result?.ObservationsWritten,
                });
        });
    }

    private static string ToWriteReceiptStatus(MemoryWriteReceiptStatus status) =>
        status.ToString().ToLowerInvariant();

    private static MemoryWriteReceiptStatus ParseWriteReceiptStatus(string? status) =>
        Enum.TryParse<MemoryWriteReceiptStatus>(status, true, out var parsed)
            ? parsed
            : MemoryWriteReceiptStatus.Queued;
}
