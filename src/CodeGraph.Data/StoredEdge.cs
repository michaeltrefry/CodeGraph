using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>A persisted edge with its endpoints' qualified names and file paths.</summary>
public sealed record StoredEdge(
    string SourceQN,
    string? SourceFilePath,
    string TargetQN,
    string? TargetFilePath,
    EdgeType Type,
    Dictionary<string, object> Properties);

/// <summary>
/// Persisted graph data a file-slice replacement removes beyond the replaced files' own nodes:
/// edges to delete (by source, target and type), and nodes without a file path (e.g. stubs) to
/// delete when the write leaves them without any edge.
/// </summary>
public sealed record GraphSliceRetractions(
    IReadOnlyList<PendingEdge> Edges,
    IReadOnlyList<string> OrphanCandidateQNs)
{
    public bool IsEmpty => Edges.Count == 0 && OrphanCandidateQNs.Count == 0;
}
