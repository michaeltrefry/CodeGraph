namespace CodeGraph.Models.Requests;

/// <summary>
/// Request to process one or more repositories.
/// </summary>
public class ProcessRequest
{
    /// <summary>
    /// Repo entries:
    ///   "TC.OrdersApi"                                          — name only (resolved via cache or stored repo_url)
    ///   "TC.OrdersApi::C:\repos\TC.OrdersApi"                   — explicit local path
    ///   "TC.OrdersApi::https://gitlab.tcdevops.com/Group/Repo"  — explicit GitLab URL
    /// </summary>
    public List<string> Repos { get; set; } = [];

    public bool ShouldIndex { get; set; } = true;
    public bool ShouldAnalyze { get; set; } = true;
    public bool SkipIfUpToDate { get; set; } = true;

    /// <summary>
    /// Include source code for all classes in analysis, not just convention-matched ones.
    /// Use for repos that don't follow the TC.* project structure.
    /// </summary>
    public bool IncludeAllSource { get; set; }
}
