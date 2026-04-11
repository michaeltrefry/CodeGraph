namespace CodeGraph.Models.Memory;

public class MemoryStoreAcceptedResult
{
    public string Status { get; set; } = "queued";
    public string ReceiptId { get; set; } = string.Empty;
    public string Source { get; set; } = "api";
    public string InputMode { get; set; } = "typed";
    public int EntitiesRequested { get; set; }
    public int ClaimsRequested { get; set; }
    public int EvidenceRequested { get; set; }
}
