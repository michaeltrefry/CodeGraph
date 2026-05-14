namespace CodeGraph.Jobs;

public static class JobTypes
{
    public const string Discover = "Discover";
    public const string ReIndexAll = "ReIndexAll";
    public const string ProcessBatchAnalysis = "ProcessBatchAnalysis";
    public const string LinkAndDetect = "LinkAndDetect";
    public const string DetectCommunities = "DetectCommunities";
    public const string RegenerateMcpDocs = "RegenerateMcpDocs";
    public const string AssistantRetentionCleanup = "AssistantRetentionCleanup";
    public const string IngestConventionEmbeddings = "IngestConventionEmbeddings";

    public static readonly IReadOnlyList<string> All =
    [
        Discover,
        ReIndexAll,
        ProcessBatchAnalysis,
        LinkAndDetect,
        DetectCommunities,
        RegenerateMcpDocs,
        AssistantRetentionCleanup,
        IngestConventionEmbeddings
    ];
}
