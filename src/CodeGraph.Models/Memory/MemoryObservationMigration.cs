namespace CodeGraph.Models.Memory;

public class MemoryObservationMigrationResult
{
    public int ObservationsRead { get; set; }
    public int ObservationsUpdated { get; set; }
    public int EntityLinksAdded { get; set; }
    public int ClaimLinksAdded { get; set; }
}
