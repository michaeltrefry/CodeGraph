namespace TC.CodeGraphApi.Services.Configuration;

public class IndexingOptions
{
    public int MaxParallelFiles { get; set; } = 8;

    /// <summary>
    /// Maximum number of repositories processed concurrently.
    /// Limits resource consumption (MySQL connections, disk I/O, Roslyn workspaces).
    /// </summary>
    public int MaxParallelRepos { get; set; } = 4;

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
