namespace CodeGraph.Models.Memory;

public class MemorySummaryRenderRequest
{
    public List<string> EntityIds { get; set; } = [];
    public List<string> ClaimIds { get; set; } = [];
    public string Style { get; set; } = "markdown";
}

public class MemorySummaryRenderResult
{
    public string Style { get; set; } = "markdown";
    public string Text { get; set; } = string.Empty;
}
