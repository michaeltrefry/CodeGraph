using CodeGraph.Data;

namespace CodeGraph.Services.Configuration;

public class CodeGraphServiceSettings
{
    public int TsPort { get; set; } = 3100;
    public CodeGraphStorageOptions StorageOptions { get; set; } = new();
    public AnalysisOptions AnalysisOptions { get; set; } = new();
    public RepositorySourceOptions RepositorySource { get; set; } = new();
    public IndexingOptions IndexingOptions { get; set; } = new();
    public ConsumerOptions ConsumerOptions { get; set; } = new();
    public WikiOptions WikiOptions { get; set; } = new();
    public RabbitMqOptions RabbitMqOptions { get; set; } = new();
}

public class ConsumerOptions
{
    public int ConsumerRetryLimit { get; set; } = 3;
    public int ConsumerRetryInitialInterval { get; set; } = 1000;
    public int ConsumerRetryIntervalIncrement { get; set; } = 1000;
}

public class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}
