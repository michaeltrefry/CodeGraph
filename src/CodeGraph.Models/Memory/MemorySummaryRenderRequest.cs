namespace CodeGraph.Models.Memory;

public class MemorySummaryRenderRequest
{
    public List<string> EntityIds { get; set; } = [];
    public List<string> ClaimIds { get; set; } = [];
    public string Style { get; set; } = "markdown";
    // The key of a work project (CG-165): only its entities and claims are rendered. Null: unscoped.
    public string? ProjectKey { get; set; }
}

public class MemorySummaryRenderResult
{
    public string Style { get; set; } = "markdown";
    public string Text { get; set; } = string.Empty;
}
