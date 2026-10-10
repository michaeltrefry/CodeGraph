namespace CodeGraph.Models.Requests;

/// <summary>
/// Requests a branch overlay index for <see cref="Repo"/> at a branch name, tag, or commit SHA.
/// </summary>
public class BranchOverlayRequest
{
    public string Repo { get; set; } = string.Empty;

    /// <summary>Branch name (e.g. <c>factory/feature-x</c>), tag, or commit SHA.</summary>
    public string Ref { get; set; } = string.Empty;
}
