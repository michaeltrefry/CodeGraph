using CodeGraph.Models.Memory;

namespace CodeGraph.Models.Messages;

public class StoreMemoryClaims
{
    public required MemoryClaimExtractionResult Extraction { get; set; }
    public string Source { get; set; } = "api";
}
