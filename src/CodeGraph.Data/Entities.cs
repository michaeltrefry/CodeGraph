namespace CodeGraph.Data;

public class RepositoryEntity
{
    public string Name { get; set; } = "";
    public string? RepoUrl { get; set; }
    public string? SourceGroup { get; set; }
    public string? LocalPath { get; set; }
    public string? DefaultBranch { get; set; } = "main";
    public string? LastCommitSha { get; set; }
    public DateTime? IndexedAt { get; set; }
    public string? Language { get; set; }
    public string? Framework { get; set; }
    public bool IsFoundational { get; set; }
    public string? Properties { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class NodeEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public string? DotnetProject { get; set; }
    public string Label { get; set; } = "";
    public string Name { get; set; } = "";
    public string QualifiedName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Properties { get; set; }
    public bool DoNotTrust { get; set; }
}

public class EdgeEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public long SourceId { get; set; }
    public long TargetId { get; set; }
    public string Type { get; set; } = "";
    public string? Properties { get; set; }
}

public class CrossRepoEdgeEntity
{
    public long Id { get; set; }
    public string SourceProject { get; set; } = "";
    public string TargetProject { get; set; } = "";
    public long SourceNodeId { get; set; }
    public long TargetNodeId { get; set; }
    public string Type { get; set; } = "";
    public string? Properties { get; set; }
}

public class FileHashEntity
{
    public string Project { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string ContentHash { get; set; } = "";
}

public class RepositorySummaryEntity
{
    public string Project { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Confidence { get; set; } = "medium";
    public string SourceHash { get; set; } = "";
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ProjectAnalysisEntity
{
    public long Id { get; set; }
    public string Repo { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Confidence { get; set; } = "medium";
    public string? Endpoints { get; set; }
    public string? Services { get; set; }
    public string? ExternalDependencies { get; set; }
    public string? DatabaseTables { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SyncStateEntity
{
    public string Project { get; set; } = "";
    public DateTime? LastSyncAt { get; set; }
    public string? LastCommitSha { get; set; }
    public string Status { get; set; } = "idle";
    public string? ErrorMessage { get; set; }
}

public class MigrationHistoryEntity
{
    public int Id { get; set; }
    public string ScriptName { get; set; } = "";
    public DateTime AppliedAt { get; set; }
}

public class AnalysisBatchEntity
{
    public long Id { get; set; }
    public string Repo { get; set; } = "";
    public string ProviderBatchId { get; set; } = "";
    public string ProviderName { get; set; } = "anthropic";
    public string ExecutionMode { get; set; } = "native_batch";
    public bool IncludeAllSource { get; set; }
    public string Status { get; set; } = "submitted";
    public int RequestCount { get; set; }
    public int CompletedCount { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class AnalysisBatchRequestEntity
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public int Sequence { get; set; }
    public string CustomId { get; set; } = "";
    public long? NodeId { get; set; }
    public string NodeLabel { get; set; } = "";
    public string? RequestPayloadJson { get; set; }
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public string? ResponseText { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class NodeAnalysisEntity
{
    public long NodeId { get; set; }
    public string Description { get; set; } = "";
    public string Confidence { get; set; } = "medium";
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class FileMetricsEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? DotnetProject { get; set; }

    // Churn (90-day window)
    public int Changes { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int AuthorCount { get; set; }
    public DateTime? LastChangeAt { get; set; }

    // Complexity
    public int ComplexityScore { get; set; }
    public int MaxNestingDepth { get; set; }
    public int DeepNestingLines { get; set; }
    public int FunctionCount { get; set; }
    public int LongestFunction { get; set; }

    // Lint (ESLint for TS/JS, Roslyn diagnostics for C#)
    public int LintErrors { get; set; }
    public int LintWarnings { get; set; }

    // Trust (0.0–1.0 composite: stability + lint quality)
    public double TrustScore { get; set; } = 0.5;

    // Coupling
    public double MaxCouplingStrength { get; set; }
    public int CouplingPartners { get; set; }

    // Knowledge Risk
    public int TruckFactor { get; set; }
    public string? TopAuthors { get; set; }

    // Composite
    public double HealthScore { get; set; } = 5.0;
    public string Role { get; set; } = "core";
    public double RiskScore { get; set; }

    public DateTime ComputedAt { get; set; }
}

public class ProjectHealthSummaryEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public string? DotnetProject { get; set; }

    public double OverallHealth { get; set; } = 5.0;
    public int TotalFiles { get; set; }
    public int HotspotCount { get; set; }
    public int AlertCount { get; set; }
    public string? TopHotspots { get; set; }

    public DateTime ComputedAt { get; set; }
}

public class ProjectHealthAnalysisEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public string? DotnetProject { get; set; }
    public string Analysis { get; set; } = "";
    public string Confidence { get; set; } = "medium";
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SecurityFindingEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public string? DotnetProject { get; set; }
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "medium";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public string? Package { get; set; }
    public string? PackageVersion { get; set; }
    public string? Advisory { get; set; }
    public DateTime ComputedAt { get; set; }
}

public class ProjectDiagnosticEntity
{
    public string Project { get; set; } = "";
    public string? DotnetProject { get; set; }
    public string Source { get; set; } = "roslyn";
    public string DiagnosticKey { get; set; } = "";
    public string DiagnosticId { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Category { get; set; }
    public string FilePath { get; set; } = "";
    public int? LineStart { get; set; }
    public int? LineEnd { get; set; }
    public DateTime ComputedAt { get; set; }
}

public class ProjectReviewRunEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? ReviewedCommitSha { get; set; }
    public string Status { get; set; } = "queued";
    public string ReviewMode { get; set; } = "standard";
    public string PromptVersion { get; set; } = "v1";
    public string? OverviewJson { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public class ProjectReviewFindingEntity
{
    public long Id { get; set; }
    public long ReviewRunId { get; set; }
    public int Ordinal { get; set; }
    public string Severity { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int? LineStart { get; set; }
    public int? LineEnd { get; set; }
    public string SuggestedImprovement { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string? ProvenanceJson { get; set; }
}

public class RepositoryReviewRunEntity
{
    public long Id { get; set; }
    public string Repo { get; set; } = "";
    public string? ReviewedCommitSha { get; set; }
    public long? BaselineReviewRunId { get; set; }
    public string? BaselineCommitSha { get; set; }
    public string Status { get; set; } = "queued";
    public string ReviewMode { get; set; } = "full";
    public string PromptVersion { get; set; } = "v1";
    public string? OverviewJson { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public class RepositoryReviewProjectSectionEntity
{
    public long Id { get; set; }
    public long ReviewRunId { get; set; }
    public string ProjectName { get; set; } = "";
    public string Overview { get; set; } = "";
    public string StrengthsJson { get; set; } = "[]";
    public string ReviewedAreasJson { get; set; } = "[]";
    public string SkippedAreasJson { get; set; } = "[]";
    public string FollowUpsJson { get; set; } = "[]";
    public bool ReusedFromBaseline { get; set; }
}

public class RepositoryReviewFindingEntity
{
    public long Id { get; set; }
    public long ReviewRunId { get; set; }
    public string? ProjectName { get; set; }
    public int Ordinal { get; set; }
    public string Severity { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int? LineStart { get; set; }
    public int? LineEnd { get; set; }
    public string SuggestedImprovement { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string? ProvenanceJson { get; set; }
}

public class ProjectSecuritySummaryEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public double SecurityScore { get; set; } = 10.0;
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
    public string? Analysis { get; set; }
    public DateTime ComputedAt { get; set; }
}

public class ConventionPageEntity
{
    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Author { get; set; } = "";
    public int Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ConventionRevisionEntity
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public int Revision { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class WikiSectionEntity
{
    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public bool IsSystem { get; set; }
    public bool AllowUserPages { get; set; } = true;
    public bool HasRawContent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class WikiPageEntity
{
    public long Id { get; set; }
    public long SectionId { get; set; }
    public long? ParentId { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? RawContent { get; set; }
    public string Author { get; set; } = "";
    public int Revision { get; set; } = 1;
    public int SortOrder { get; set; }
    public bool IsAutoGenerated { get; set; }
    public int Depth { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class WikiRevisionEntity
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public int Revision { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? RawContent { get; set; }
    public string Author { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class WikiAttachmentEntity
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public string Filename { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class ExclusionRuleEntity
{
    public long Id { get; set; }
    public string TargetType { get; set; } = "";
    public string TargetValue { get; set; } = "";
    public string ExclusionType { get; set; } = "";
    public string? Reason { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class JobScheduleEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string JobType { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string CronExpression { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    public string ArgsJson { get; set; } = "{}";
    public DateTime NextRunUtc { get; set; }
    public DateTime? LastRunStartedUtc { get; set; }
    public DateTime? LastRunCompletedUtc { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime? LeaseAcquiredUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
