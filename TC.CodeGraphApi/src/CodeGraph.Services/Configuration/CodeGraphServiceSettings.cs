using CodeGraph.Data;

namespace CodeGraph.Services.Configuration;

public class CodeGraphServiceSettings
{
    public int TsPort { get; set; } = 3100;
    public CodeGraphStorageOptions StorageOptions { get; set; } = new CodeGraphStorageOptions();
    public AnalysisOptions AnalysisOptions { get; set; } = new AnalysisOptions();
    public GitLabOptions GitLabOptions { get; set; } = new GitLabOptions();
    public IndexingOptions IndexingOptions { get; set; } = new IndexingOptions();
    public ConsumerOptions ConsumerOptions { get; set; } = new ConsumerOptions();
    public WikiOptions WikiOptions { get; set; } = new WikiOptions();
    public AuthOptions AuthOptions { get; set; } = new AuthOptions();
}

public class ConsumerOptions
{
    public int ConsumerRetryLimit { get; set; } = 3;
    public int ConsumerRetryInitialInterval { get; set; } = 1000;
    public int ConsumerRetryIntervalIncrement { get; set; } = 1000;
}