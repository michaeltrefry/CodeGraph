using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work.Imports;

/// <summary>
/// Files stored with work items (imported Shortcut attachments). Reuses the wiki's hardened file
/// store (<see cref="AttachmentStorage"/>: confined to its root, no symlink escapes) under its own
/// root, <see cref="WorkImportOptions.AttachmentStoragePath"/> (default <c>uploads/work-items</c>). The API
/// (which serves the files) and the Jobs host (whose WorkImport job downloads them) must see the same
/// directory: docker-compose mounts the codegraph-uploads volume at /app/uploads in both.
/// </summary>
public interface IWorkAttachmentStorage
{
    Task<(string Path, long Size)> SaveAsync(byte[] content, CancellationToken ct);
    Stream OpenRead(string storagePath);

    /// <summary>Whether the stored file is there and readable (a record whose file is missing counts as not stored).</summary>
    bool Exists(string storagePath);
}

public sealed class WorkAttachmentStorage(IOptions<WorkImportOptions> importOptions, IOptions<WikiOptions> wikiOptions) : IWorkAttachmentStorage, IDisposable
{
    // AttachmentStorage keeps files in a numbered container; work items share one.
    private const long Container = 1;

    private readonly AttachmentStorage storage = new(
        importOptions.Value.AttachmentStoragePath is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetDirectoryName(wikiOptions.Value.AttachmentStoragePath) ?? "uploads", "work-items"));

    public async Task<(string Path, long Size)> SaveAsync(byte[] content, CancellationToken ct)
    {
        using var stream = new MemoryStream(content, writable: false);
        return await storage.CreateAsync(Container, stream);
    }

    public Stream OpenRead(string storagePath) => storage.OpenRead(storagePath);

    public bool Exists(string storagePath)
    {
        try
        {
            using var stream = storage.OpenRead(storagePath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose() => storage.Dispose();
}
