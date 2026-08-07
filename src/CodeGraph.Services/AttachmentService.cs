using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services;

public class AttachmentService(
    IWikiStore store,
    IOptions<WikiOptions> wikiOptionsAccessor,
    ILogger<AttachmentService> logger) : IAttachmentService
{
    private readonly WikiOptions wikiOptions = wikiOptionsAccessor.Value;

    public async Task<IReadOnlyList<WikiAttachmentResponse>> ListAsync(long pageId)
    {
        var attachments = await store.ListAttachmentsAsync(pageId);
        return attachments.Select(ToResponse).ToList();
    }

    public async Task<WikiAttachmentResponse?> UploadAsync(long pageId, string filename, string contentType, Stream content, string username)
    {
        var page = await store.GetPageByIdAsync(pageId);
        if (page is null) return null;

        var displayName = SanitizeDisplayName(filename);
        var storageRoot = GetStorageRoot();
        Directory.CreateDirectory(storageRoot);
        EnsureNoSymbolicLinks(storageRoot, storageRoot);

        var pageDirectory = GetContainedPath(storageRoot, pageId.ToString());
        if (Directory.Exists(pageDirectory))
            EnsureNoSymbolicLinks(storageRoot, pageDirectory);
        Directory.CreateDirectory(pageDirectory);
        EnsureNoSymbolicLinks(storageRoot, pageDirectory);

        string storagePath = "";
        FileStream? destination = null;
        for (var attempt = 0; attempt < 10 && destination is null; attempt++)
        {
            storagePath = GetContainedPath(pageDirectory, Guid.NewGuid().ToString("N"));
            EnsureNoSymbolicLinks(storageRoot, pageDirectory);
            try
            {
                destination = new FileStream(
                    storagePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (File.Exists(storagePath) || Directory.Exists(storagePath))
            {
                // An opaque name collision must never turn into an overwrite.
            }
        }

        if (destination is null)
            throw new IOException("Could not allocate a unique attachment storage path.");

        try
        {
            await using (destination)
                await content.CopyToAsync(destination);
        }
        catch
        {
            TryDeleteCreatedFile(storageRoot, storagePath);
            throw;
        }

        var fileInfo = new FileInfo(storagePath);
        var entity = new WikiAttachmentEntity
        {
            PageId = pageId,
            Filename = displayName,
            StoragePath = storagePath,
            ContentType = contentType,
            SizeBytes = fileInfo.Length,
            UploadedBy = username,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            entity = await store.CreateAttachmentAsync(entity);
        }
        catch
        {
            TryDeleteCreatedFile(storageRoot, storagePath);
            throw;
        }

        return ToResponse(entity);
    }

    public async Task<(Stream Content, string ContentType, string Filename)?> GetAsync(long attachmentId)
    {
        var entity = await store.GetAttachmentByIdAsync(attachmentId);
        if (entity is null || !TryGetSafeStoragePath(entity.StoragePath, out var storagePath)) return null;
        if (!File.Exists(storagePath)) return null;

        var stream = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (stream, entity.ContentType, entity.Filename);
    }

    public async Task<bool> DeleteAsync(long attachmentId)
    {
        var entity = await store.GetAttachmentByIdAsync(attachmentId);
        if (entity is null) return false;

        if (!TryGetSafeStoragePath(entity.StoragePath, out var storagePath))
            return false;

        try
        {
            if (File.Exists(storagePath))
                File.Delete(storagePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete attachment file {Path}", storagePath);
            return false;
        }

        await store.DeleteAttachmentAsync(entity);
        return true;
    }

    private WikiAttachmentResponse ToResponse(WikiAttachmentEntity entity) => new(
        entity.Id,
        entity.Filename,
        entity.ContentType,
        entity.SizeBytes,
        entity.UploadedBy,
        $"/api/wiki/attachments/{entity.Id}/{Uri.EscapeDataString(entity.Filename)}",
        entity.CreatedAt);

    private string GetStorageRoot() => Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(wikiOptions.AttachmentStoragePath));

    private bool TryGetSafeStoragePath(string path, out string storagePath)
    {
        storagePath = "";
        try
        {
            var storageRoot = GetStorageRoot();
            storagePath = GetContainedPath(storageRoot, path);
            EnsureNoSymbolicLinks(storageRoot, storagePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Rejected unsafe attachment storage path {Path}", path);
            storagePath = "";
            return false;
        }
    }

    private void TryDeleteCreatedFile(string storageRoot, string storagePath)
    {
        try
        {
            EnsureNoSymbolicLinks(storageRoot, storagePath);
            if (File.Exists(storagePath))
                File.Delete(storagePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up attachment file {Path}", storagePath);
        }
    }

    private static string SanitizeDisplayName(string filename)
    {
        var normalized = (filename ?? "").Replace('\\', '/');
        var displayName = normalized[(normalized.LastIndexOf('/') + 1)..].Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        displayName = new string(displayName
            .Select(c => char.IsControl(c) || c is '/' or '\\' || invalidCharacters.Contains(c) ? '_' : c)
            .ToArray());

        if (string.IsNullOrWhiteSpace(displayName) || displayName is "." or "..")
            return "attachment";

        return displayName.Length <= 255 ? displayName : displayName[..255];
    }

    private static string GetContainedPath(string root, string path)
    {
        var candidate = Path.GetFullPath(path, root);
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("Attachment path escapes the configured storage root.");
        }

        return candidate;
    }

    private static void EnsureNoSymbolicLinks(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(path, fullRoot);
        if (!string.Equals(candidate, fullRoot, PathComparison))
            candidate = GetContainedPath(fullRoot, candidate);

        ThrowIfSymbolicLink(fullRoot);

        var relative = Path.GetRelativePath(fullRoot, candidate);
        var current = fullRoot;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            ThrowIfSymbolicLink(current);
        }
    }

    private static void ThrowIfSymbolicLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Symbolic links are not allowed in attachment storage paths.");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
