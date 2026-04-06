namespace CodeGraph.Models.Requests;

public class DiscoverRequest
{
    public bool ShouldIndex { get; set; } = true;
    public bool ShouldAnalyze { get; set; } = true;
    public bool SkipIfUpToDate { get; set; } = true;
    public bool IncludeAllSource { get; set; }

    /// <summary>
    /// Regex pattern to filter discovered repos by name (e.g., "^TC\." for all TC repos,
    /// "TC\.Account" for account-related repos). Case-insensitive. Null = no filter.
    /// </summary>
    public string? NamePattern { get; set; }

    /// <summary>
    /// Max number of new/changed repos to publish. Repos that are already synced
    /// (and would be skipped by the consumer) do not count against this limit.
    /// Null = no limit (publish all discovered repos).
    /// </summary>
    public int? Limit { get; set; }
}
