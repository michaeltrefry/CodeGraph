using CodeGraph.Data;
using Dapper;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlWorkStore : IWorkAttachmentStore
{
    public async Task<AttachmentRecord?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AttachmentRecord>(new CommandDefinition(
            "SELECT id AS Id, item_id AS ItemId, filename AS Filename, content_type AS ContentType, size_bytes AS SizeBytes, storage_path AS StoragePath " +
            "FROM work_item_attachments WHERE id = @attachmentId",
            new { attachmentId }, cancellationToken: ct));
    }
}
