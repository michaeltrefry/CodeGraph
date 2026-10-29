using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work.Imports;

/// <summary>
/// Files stored with work items (imported Shortcut attachments). Reuses the wiki's hardened file
/// store (<see cref="AttachmentStorage"/>: confined to its root, no symlink escapes) under its own
/// root, by default the wiki attachment root's <c>work-items</c> folder.
/// </summary>
public interface IWorkAttachmentStorage
{
    Task<(string Path, long Size)> SaveAsync(byte[] content, CancellationToken ct);
    Stream OpenRead(string storagePath);
}

public sealed class WorkAttachmentStorage(IOptions<WorkImportOptions> importOptions, IOptions<WikiOptions> wikiOptions) : IWorkAttachmentStorage, IDisposable
{
    // AttachmentStorage keeps files in a numbered container; work items share one.
    private const long Container = 1;

    private readonly AttachmentStorage storage = new(
        importOptions.Value.AttachmentStoragePath is { Length: > 0 } configured
            ? configured
            : Path.Combine(wikiOptions.Value.AttachmentStoragePath, "work-items"));

    public async Task<(string Path, long Size)> SaveAsync(byte[] content, CancellationToken ct)
    {
        using var stream = new MemoryStream(content, writable: false);
        return await storage.CreateAsync(Container, stream);
    }

    public Stream OpenRead(string storagePath) => storage.OpenRead(storagePath);

    public void Dispose() => storage.Dispose();
}
