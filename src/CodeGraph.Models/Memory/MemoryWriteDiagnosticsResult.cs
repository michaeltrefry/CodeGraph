namespace CodeGraph.Models.Memory;

public class MemoryWriteDiagnosticsResult
{
    public string Username { get; set; } = "default";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public int QueuedCount { get; set; }
    public int StaleQueuedCount { get; set; }
    public int ProcessingCount { get; set; }
    public int RetryingCount { get; set; }
    public int CompletedCount { get; set; }
    public int FailedCount { get; set; }
    public int SubmissionFailureCount { get; set; }
    public int ProcessingFailureCount { get; set; }
    public int StaleProcessingCount { get; set; }
    public int StaleAfterMinutes { get; set; } = 15;
    public double? OldestQueuedAgeMinutes { get; set; }
    public double? OldestProcessingAgeMinutes { get; set; }
    public List<MemoryWriteReceipt> StaleQueuedReceipts { get; set; } = [];
    public List<MemoryWriteReceipt> StaleProcessingReceipts { get; set; } = [];
    public List<MemoryWriteReceipt> RetryingReceipts { get; set; } = [];
    public List<MemoryWriteReceipt> RecentFailedReceipts { get; set; } = [];
}
