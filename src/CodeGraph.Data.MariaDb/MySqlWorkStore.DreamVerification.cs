using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore
{
    private sealed class DreamGraphNodeRow
    {
        public long Id { get; init; }
        public string Repository { get; init; } = "";
        public string Label { get; init; } = "";
        public string Name { get; init; } = "";
        public string QualifiedName { get; init; } = "";
        public string? FilePath { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public string? Properties { get; init; }
    }

    private static async Task<List<NodeRef>> ResolveDreamVerificationNodesAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid projectId, IReadOnlyList<EntryNodeRow> references, CancellationToken ct)
    {
        if (references.Count == 0) return [];
        var ids = references.Select(node => node.NodeId).Distinct().ToArray();
        // Current locking reads protect both metadata-only proofs and file locations from index
        // updates through commit. Recreated or removed nodes require a fresh evidence decision.
        var nodes = (await connection.QueryAsync<DreamGraphNodeRow>(new CommandDefinition(
            $"SELECT n.id,n.repository,n.label,n.name,n.qualified_name,n.file_path,n.start_line,n.end_line,n.properties FROM nodes n WHERE n.id IN @ids AND EXISTS(SELECT 1 FROM work_project_repositories r WHERE r.project_id=@projectId AND {CoercibleText("r.repository_name")}=n.repository) ORDER BY n.id LOCK IN SHARE MODE",
            new { ids, projectId }, transaction, cancellationToken: ct))).ToList();
        if (nodes.Count != ids.Length)
            throw new WorkException(WorkErrorCodes.Forbidden, "A referenced graph node no longer exists in this project. Read the current graph and propose a correction; nothing was verified.");
        foreach (var node in nodes.Where(node => string.IsNullOrWhiteSpace(node.FilePath)))
        {
            var current = DreamGraphIdentity.Fingerprint(node.Id, node.Repository, node.Label, node.Name, node.QualifiedName, node.FilePath ?? "", node.StartLine, node.EndLine, node.Properties ?? "{}");
            if (DreamExecutionScope.Current!.ReadNodeHash(node.Id) != current)
                throw new WorkException(WorkErrorCodes.Forbidden, "The graph node being verified was not read at its current revision. Read its current metadata again; nothing was changed.");
        }
        return nodes.Select(node => new NodeRef(node.Id, node.Repository, node.QualifiedName, node.FilePath)).ToList();
    }
}
