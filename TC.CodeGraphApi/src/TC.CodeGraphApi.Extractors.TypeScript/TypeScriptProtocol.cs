using System.Text.Json.Serialization;

namespace TC.CodeGraphApi.Extractors.TypeScript;

public record ExtractProjectRequest
{
    [JsonPropertyName("projectName")] public required string ProjectName { get; init; }
    [JsonPropertyName("rootPath")]    public required string RootPath { get; init; }
    [JsonPropertyName("tsconfigPath")] public required string TsconfigPath { get; init; }
}

public record ExtractProjectResponse
{
    [JsonPropertyName("nodes")]            public List<GraphNodeDto> Nodes { get; init; } = [];
    [JsonPropertyName("edges")]            public List<PendingEdgeDto> Edges { get; init; } = [];
    [JsonPropertyName("unresolvedImports")] public List<UnresolvedImportDto> UnresolvedImports { get; init; } = [];
    [JsonPropertyName("unresolvedCalls")]   public List<UnresolvedCallDto> UnresolvedCalls { get; init; } = [];
    [JsonPropertyName("diagnostics")]      public List<string>? Diagnostics { get; init; }
}

public record GraphNodeDto
{
    [JsonPropertyName("label")]        public required string Label { get; init; }
    [JsonPropertyName("name")]         public required string Name { get; init; }
    [JsonPropertyName("qualifiedName")] public required string QualifiedName { get; init; }
    [JsonPropertyName("filePath")]     public string FilePath { get; init; } = "";
    [JsonPropertyName("startLine")]    public int StartLine { get; init; }
    [JsonPropertyName("endLine")]      public int EndLine { get; init; }
    [JsonPropertyName("properties")]   public Dictionary<string, object> Properties { get; init; } = new();
}

public record PendingEdgeDto
{
    [JsonPropertyName("sourceQN")]   public required string SourceQN { get; init; }
    [JsonPropertyName("targetQN")]   public required string TargetQN { get; init; }
    [JsonPropertyName("type")]       public required string Type { get; init; }
    [JsonPropertyName("properties")] public Dictionary<string, object>? Properties { get; init; }
}

public record UnresolvedImportDto
{
    [JsonPropertyName("fileQN")]            public required string FileQN { get; init; }
    [JsonPropertyName("importedNamespace")] public required string ImportedNamespace { get; init; }
}

public record UnresolvedCallDto
{
    [JsonPropertyName("callerQN")]    public required string CallerQN { get; init; }
    [JsonPropertyName("calleeName")]  public required string CalleeName { get; init; }
    [JsonPropertyName("receiverType")] public string? ReceiverType { get; init; }
    [JsonPropertyName("confidence")]  public double Confidence { get; init; }
}

// Lint endpoint DTOs
public record LintProjectRequest
{
    [JsonPropertyName("repoPath")] public required string RepoPath { get; init; }
    [JsonPropertyName("files")]    public List<string>? Files { get; init; }
}

public record LintProjectResponse
{
    [JsonPropertyName("results")]     public List<FileLintResultDto> Results { get; init; } = [];
    [JsonPropertyName("diagnostics")] public List<string>? Diagnostics { get; init; }
}

public record FileLintResultDto
{
    [JsonPropertyName("filePath")]     public string FilePath { get; init; } = "";
    [JsonPropertyName("errorCount")]   public int ErrorCount { get; init; }
    [JsonPropertyName("warningCount")] public int WarningCount { get; init; }
}
