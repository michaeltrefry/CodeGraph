namespace CodeGraph.Services.Configuration;

public class IndexingOptions
{
    public int MaxParallelFiles { get; set; } = 8;

    /// <summary>
    /// Maximum number of repositories processed concurrently.
    /// Limits resource consumption (Neo4j connections, disk I/O, Roslyn workspaces).
    /// </summary>
    public int MaxParallelRepos { get; set; } = 4;

    /// <summary>
    /// Recompute fleet-wide communities after each repository finishes indexing.
    /// Disable during bulk ingestion to avoid rerunning Louvain on every repo.
    /// </summary>
    public bool DetectCommunitiesAfterIndexing { get; set; } = false;

    public int MaxFileSizeKb { get; set; } = 512;
    public string[] SkipPatterns { get; set; } =
    [
        "**/bin/**", "**/obj/**", "**/node_modules/**",
        "**/wwwroot/lib/**", "**/*.min.js", "**/.git/**",
        "**/packages/**", "**/TestResults/**"
    ];

    public string[] FoundationalRepos { get; set; } = [];
    public string? ConventionsPath { get; set; }
}
