namespace CodeGraph.Models.Memory;

public class MemoryPathExplanation
{
    public required string SeedId { get; set; }
    public required string DestinationId { get; set; }
    public int HopCount { get; set; }
    public double ScoreContribution { get; set; }
    public List<string> EdgeSequence { get; set; } = [];
}
