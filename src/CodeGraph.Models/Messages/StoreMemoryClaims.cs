using CodeGraph.Models.Memory;

namespace CodeGraph.Models.Messages;

public class StoreMemoryClaims
{
    public string? ReceiptId { get; set; }
    public string InputMode { get; set; } = "typed";
    public required MemoryClaimExtractionResult Extraction { get; set; }
    public string Source { get; set; } = "api";
}
