using CodeGraph.Models.Memory;

namespace CodeGraph.Models.Messages;

public class StoreMemory
{
    public required MemoryExtractionResult Extraction { get; set; }
    public string Source { get; set; } = "api";
}
