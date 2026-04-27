namespace CodeGraph.Memory.Client;

public sealed class MemoryClientOptions
{
    public const string SectionPath = "CodeGraph:Memory";
    public const string DefaultHttpClientName = "CodeGraph.Memory";

    public string BaseUrl { get; set; } = "";
    public string Audience { get; set; } = "codegraph-memory";
    public string HttpClientName { get; set; } = DefaultHttpClientName;
    public int MaxTransientAttempts { get; set; } = 3;
}
