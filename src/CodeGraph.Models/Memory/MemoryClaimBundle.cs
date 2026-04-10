namespace CodeGraph.Models.Memory;

public class MemoryClaimBundle
{
    public required MemoryClaim Claim { get; set; }
    public List<MemoryClaim> FactGroupPeers { get; set; } = [];
    public List<MemoryClaim> SupersessionChain { get; set; } = [];
    public List<MemoryClaim> Conflicts { get; set; } = [];
    public List<MemoryEvidence> Evidence { get; set; } = [];
    public List<MemoryObservation> Observations { get; set; } = [];
}
