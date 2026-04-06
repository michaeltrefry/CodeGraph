namespace TC.CodeGraphApi.Models;

public record RepoCluster
{
    public long Id { get; init; }
    public required string ProjectName { get; init; }
    public int ClusterId { get; init; }
    public string? ClusterLabel { get; init; }
    public decimal ModularityScore { get; init; }
    public int Level { get; init; }
    public decimal BetweennessCentrality { get; init; }
    public DateTime ComputedAt { get; init; }
}
