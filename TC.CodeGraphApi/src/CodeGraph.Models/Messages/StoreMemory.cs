using CodeGraph.Models.Memory;

namespace CodeGraph.Models.Messages;

public class StoreMemory
{
    public required string Username { get; set; }
    public required MemoryExtractionResult Extraction { get; set; }
    public string Source { get; set; } = "api";
}
