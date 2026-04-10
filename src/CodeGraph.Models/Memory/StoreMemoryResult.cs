namespace CodeGraph.Models.Memory;

public class StoreMemoryResult
{
    public int NodesWritten { get; set; }
    public int EdgesWritten { get; set; }
    public int ConflictsDetected { get; set; }
    public int ClaimsWritten { get; set; }
    public int EvidenceWritten { get; set; }
    public int ObservationsWritten { get; set; }
}
