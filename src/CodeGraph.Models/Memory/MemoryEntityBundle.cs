namespace CodeGraph.Models.Memory;

public class MemoryEntityBundle
{
    public required MemoryEntity Entity { get; set; }
    public List<MemoryClaim> ActiveClaims { get; set; } = [];
    public List<MemoryClaim> ConflictingClaims { get; set; } = [];
    public List<MemoryClaim> SupersededClaims { get; set; } = [];
    public List<MemoryEntityEdge> NeighborEdges { get; set; } = [];
    public List<MemoryObservation> Observations { get; set; } = [];
}
