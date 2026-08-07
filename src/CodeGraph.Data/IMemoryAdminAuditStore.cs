namespace CodeGraph.Data;

public interface IMemoryAdminAuditStore
{
    Task<long> CreatePendingAsync(MemoryAdminAuditEntity audit, CancellationToken ct = default);
    Task SetOutcomeAsync(
        long auditId,
        string outcomeStatus,
        bool succeeded,
        string? errorType,
        CancellationToken ct = default);
}

public sealed class MemoryAdminAuditEntity
{
    public long Id { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public required string ActorUsername { get; set; }
    public required string TargetUsername { get; set; }
    public required string Operation { get; set; }
    public bool DryRun { get; set; }
    public string OutcomeStatus { get; set; } = "pending";
    public bool? Succeeded { get; set; }
    public string? ErrorType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
