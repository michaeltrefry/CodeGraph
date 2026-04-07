namespace CodeGraph.Models.Requests;

/// <summary>
/// Request to process one or more repositories.
/// </summary>
public class ProcessRequest
{
    /// <summary>
    /// Repo entries:
    ///   "orders-api"                                 — name only (resolved via cache or stored repo_url)
    ///   "orders-api::C:\repos\orders-api"            — explicit local path
    ///   "orders-api::https://example.com/orders-api" — explicit remote URL
    /// </summary>
    public List<string> Repos { get; set; } = [];

    public bool ShouldIndex { get; set; } = true;
    public bool ShouldAnalyze { get; set; } = true;
    public bool SkipIfUpToDate { get; set; } = true;

    /// <summary>
    /// Include source code for all classes in analysis, not just convention-matched ones.
    /// Use for repos that do not follow the usual controller/service/consumer conventions.
    /// </summary>
    public bool IncludeAllSource { get; set; }
}
