namespace CodeGraph.Data;

/// <summary>The files stored with imported work items (sc-27876).</summary>
public interface IWorkAttachmentStore
{
    Task<AttachmentRecord?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct = default);
}

public sealed record AttachmentRecord(Guid Id, Guid ItemId, string Filename, string ContentType, long SizeBytes, string? StoragePath);
