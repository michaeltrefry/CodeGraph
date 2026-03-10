namespace TC.CodeGraphApi.Data;

public class ProjectEntity
{
    public string Name { get; set; } = "";
    public string? RepoUrl { get; set; }
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
    public string Label { get; set; } = "";
    public string Name { get; set; } = "";
    public string QualifiedName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Properties { get; set; }
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

public class ProjectSummaryEntity
{
    public string Project { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Confidence { get; set; } = "medium";
    public string SourceHash { get; set; } = "";
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
