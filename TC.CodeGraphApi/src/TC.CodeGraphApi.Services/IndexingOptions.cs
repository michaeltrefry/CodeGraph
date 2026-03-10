namespace TC.CodeGraphApi.Services;

public class IndexingOptions
{
    public int MaxParallelFiles { get; set; } = 8;
    public int MaxFileSizeKb { get; set; } = 512;
    public string[] SkipPatterns { get; set; } =
    [
        "**/bin/**", "**/obj/**", "**/node_modules/**",
        "**/wwwroot/lib/**", "**/*.min.js", "**/.git/**",
        "**/packages/**", "**/TestResults/**"
    ];
}
