namespace CodeGraph.Models.Requests;

/// <summary>
/// Requests a branch overlay index for <see cref="Repo"/> at a branch name, tag, or commit SHA.
/// </summary>
public class BranchOverlayRequest
{
    public string Repo { get; set; } = string.Empty;

    /// <summary>Branch name (e.g. <c>factory/feature-x</c>), tag, or commit SHA.</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>
    /// When true, the ref is resolved to its commit at request time and a live overlay (queued,
    /// indexing, or ready against the current default-branch index commit, and within its TTL)
    /// for the same repository and commit is returned instead of creating a new one. Default
    /// false keeps the original always-create behavior.
    /// </summary>
    public bool ReuseLive { get; set; }
}
