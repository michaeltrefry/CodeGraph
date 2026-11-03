namespace CodeGraph.Memory.Client;

public sealed class MemoryClientOptions
{
    public const string SectionPath = "CodeGraph:Memory";
    public const string DefaultHttpClientName = "CodeGraph.Memory";

    public string BaseUrl { get; set; } = "";
    public string Audience { get; set; } = "codegraph-memory";
    public string HttpClientName { get; set; } = DefaultHttpClientName;
    public int MaxTransientAttempts { get; set; } = 3;

    /// <summary>
    /// How long a project-scope scan on the memory host (CG-167/CG-172) may take: one project's full
    /// backfill, or one span of the reconciliation's range backfill. Longer than the HTTP client's
    /// default 100 seconds, since the host runs several bounded statements for it.
    /// </summary>
    public int ProjectScopeScanTimeoutSeconds { get; set; } = 900;
}
