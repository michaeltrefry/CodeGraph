namespace CodeGraph.Models.Memory;

public class MemoryWriteReceipt
{
    public required string Id { get; set; }
    public string Source { get; set; } = "api";
    public string InputMode { get; set; } = "typed";
    public MemoryWriteReceiptStatus Status { get; set; } = MemoryWriteReceiptStatus.Queued;
    public int EntitiesRequested { get; set; }
    public int ClaimsRequested { get; set; }
    public int EvidenceRequested { get; set; }
    public int AttemptCount { get; set; }
    public int NodesWritten { get; set; }
    public int EdgesWritten { get; set; }
    public int ConflictsDetected { get; set; }
    public int ClaimsWritten { get; set; }
    public int EvidenceWritten { get; set; }
    public int ObservationsWritten { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum MemoryWriteReceiptStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
}
