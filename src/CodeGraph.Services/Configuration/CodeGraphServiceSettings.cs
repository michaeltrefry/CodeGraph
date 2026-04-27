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
    public McpOptions McpOptions { get; set; } = new();
    public AuthOptions AuthOptions { get; set; } = new();
    public AssistantRetentionOptions AssistantRetentionOptions { get; set; } = new();
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

public class McpOptions
{
    public bool RequirePersonalAccessToken { get; set; }
}

public class AuthOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string Audience { get; set; } = "codegraph-api";
    public string ClientId { get; set; } = "codegraph-web";
    public string Scope { get; set; } = "openid profile email";
    public string AuthorizationUrl { get; set; } = "";
    public string TokenUrl { get; set; } = "";
    public string EndSessionUrl { get; set; } = "";
    public string[] AllowedOrigins { get; set; } = [];
    public string[] ValidAudiences { get; set; } = [];
    public bool RequireHttpsMetadata { get; set; } = true;
    public string LocalDevUsername { get; set; } = "local-admin";
    public bool LocalDevIsAdmin { get; set; } = true;
}

public class AssistantRetentionOptions
{
    public int StaleActiveRunMinutes { get; set; } = 120;
    public int TerminalRunRetentionDays { get; set; } = 90;
    public int EventRetentionDays { get; set; } = 90;
    public int ChatMessageRetentionDays { get; set; } = 180;
    public int DebugExchangeRetentionDays { get; set; } = 30;
    public int DebugTraceAuditRetentionDays { get; set; } = 180;
    public int BatchSize { get; set; } = 1000;
}
