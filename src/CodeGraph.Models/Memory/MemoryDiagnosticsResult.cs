namespace CodeGraph.Models.Memory;

public class MemoryDiagnosticsResult
{
    public string Username { get; set; } = "default";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public bool EmbeddingAvailable { get; set; }
    public bool RetrievalDegraded { get; set; }
    public bool WriteDegraded { get; set; }
    public int EntityCount { get; set; }
    public int ClaimCount { get; set; }
    public int ActiveClaimCount { get; set; }
    public int ConflictedClaimCount { get; set; }
    public int SupersededClaimCount { get; set; }
    public int DeprecatedClaimCount { get; set; }
    public int SeedAliasCount { get; set; }
    public int ObservationCount { get; set; }
    public int EvidenceCount { get; set; }
    public int OrphanObservationCount { get; set; }
    public int OrphanEvidenceCount { get; set; }
    public List<string> HealthSignals { get; set; } = [];
    public MemoryWriteDiagnosticsResult WriteDiagnostics { get; set; } = new();
}
