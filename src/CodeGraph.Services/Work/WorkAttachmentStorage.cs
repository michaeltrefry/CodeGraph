using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work;

/// <summary>
/// Files stored with work items (imported Shortcut attachments). Reuses the wiki's hardened file
/// store (<see cref="AttachmentStorage"/>: confined to its root, no symlink escapes) under its own
/// root, <see cref="WorkAttachmentOptions.StoragePath"/> (default <c>uploads/work-items</c>, on the
/// codegraph-uploads volume in docker-compose).
/// </summary>
public interface IWorkAttachmentStorage
{
    Stream OpenRead(string storagePath);
}

public sealed class WorkAttachmentStorage(IOptions<WorkAttachmentOptions> options, IOptions<WikiOptions> wikiOptions) : IWorkAttachmentStorage, IDisposable
{
    private readonly AttachmentStorage storage = new(
        options.Value.StoragePath is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetDirectoryName(wikiOptions.Value.AttachmentStoragePath) ?? "uploads", "work-items"));

    public Stream OpenRead(string storagePath) => storage.OpenRead(storagePath);

    public void Dispose() => storage.Dispose();
}
