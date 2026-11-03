using CodeGraph.Models.Memory;

namespace CodeGraph.Models.Messages;

public class StoreMemoryClaims
{
    public required string Username { get; set; }
    public string? ReceiptId { get; set; }
    public string InputMode { get; set; } = "typed";
    public required MemoryClaimExtractionResult Extraction { get; set; }
    public string Source { get; set; } = "api";

    /// <summary>The key of the work project the written entities and claims are scoped to, if any.</summary>
    public string? ProjectKey { get; set; }
}
