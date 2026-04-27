namespace CodeGraph.Indexer.Client;

public sealed class IndexerClientOptions
{
    public const string SectionPath = "CodeGraph:Indexer";
    public const string DefaultHttpClientName = "CodeGraph.Indexer";

    public string BaseUrl { get; set; } = "";
    public string Audience { get; set; } = "codegraph-indexer";
    public string HttpClientName { get; set; } = DefaultHttpClientName;
}
