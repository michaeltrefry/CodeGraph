using System.Text.Json.Serialization;

namespace CodeGraph.Extractors.TypeScript;

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
    [JsonPropertyName("workspacePackages")] public List<WorkspacePackageDto> WorkspacePackages { get; init; } = [];
    [JsonPropertyName("resolvedImports")]  public List<ResolvedImportDto> ResolvedImports { get; init; } = [];
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

public record ResolvedImportDto
{
    [JsonPropertyName("fileQN")] public required string FileQN { get; init; }
    [JsonPropertyName("filePath")] public required string FilePath { get; init; }
    [JsonPropertyName("importedNamespace")] public required string ImportedNamespace { get; init; }
    [JsonPropertyName("importKind")] public required string ImportKind { get; init; }
    [JsonPropertyName("classification")] public required string Classification { get; init; }
    [JsonPropertyName("resolvedFilePath")] public string? ResolvedFilePath { get; init; }
    [JsonPropertyName("targetFileQN")] public string? TargetFileQN { get; init; }
    [JsonPropertyName("targetWorkspacePackage")] public string? TargetWorkspacePackage { get; init; }
    [JsonPropertyName("targetPackageQN")] public string? TargetPackageQN { get; init; }
    [JsonPropertyName("externalPackageName")] public string? ExternalPackageName { get; init; }
    [JsonPropertyName("diagnostic")] public string? Diagnostic { get; init; }
    [JsonPropertyName("isBarrel")] public bool? IsBarrel { get; init; }
    [JsonPropertyName("barrelTargetFilePath")] public string? BarrelTargetFilePath { get; init; }
}

public record WorkspacePackageDto
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("qualifiedName")] public required string QualifiedName { get; init; }
    [JsonPropertyName("rootPath")] public required string RootPath { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("packageJsonPath")] public string? PackageJsonPath { get; init; }
    [JsonPropertyName("tsconfigPath")] public string? TsconfigPath { get; init; }
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
