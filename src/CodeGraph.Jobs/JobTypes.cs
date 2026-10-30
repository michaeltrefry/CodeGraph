namespace CodeGraph.Jobs;

public static class JobTypes
{
    public const string DreamProjects = "DreamProjects";
    public const string Discover = "Discover";
    public const string ReIndexAll = "ReIndexAll";
    public const string ProcessBatchAnalysis = "ProcessBatchAnalysis";
    public const string LinkAndDetect = "LinkAndDetect";
    public const string DetectCommunities = "DetectCommunities";
    public const string RegenerateMcpDocs = "RegenerateMcpDocs";
    public const string AssistantRetentionCleanup = "AssistantRetentionCleanup";
    public const string IngestConventionEmbeddings = "IngestConventionEmbeddings";
    public const string PruneWorkSessions = "PruneWorkSessions";
    public const string PruneWorkMessages = "PruneWorkMessages";
    public const string WorkImport = "WorkImport";
    public const string IndexerRunRetention = "IndexerRunRetention";

    public static readonly IReadOnlyList<string> All =
    [
        DreamProjects,
        Discover,
        ReIndexAll,
        ProcessBatchAnalysis,
        LinkAndDetect,
        DetectCommunities,
        RegenerateMcpDocs,
        AssistantRetentionCleanup,
        IngestConventionEmbeddings,
        PruneWorkSessions,
        PruneWorkMessages,
        WorkImport,
        IndexerRunRetention
    ];
}
