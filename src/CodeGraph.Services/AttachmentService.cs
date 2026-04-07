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
        return attachments.Select(a => new WikiAttachmentResponse(
            a.Id, a.Filename, a.ContentType, a.SizeBytes, a.UploadedBy,
            $"/api/wiki/attachments/{a.Id}/{a.Filename}",
            a.CreatedAt)).ToList();
    }

    public async Task<WikiAttachmentResponse?> UploadAsync(long pageId, string filename, string contentType, Stream content, string username)
    {
        var page = await store.GetPageByIdAsync(pageId);
        if (page is null) return null;

        // Ensure storage directory exists
        var dir = Path.Combine(wikiOptions.AttachmentStoragePath, pageId.ToString());
        Directory.CreateDirectory(dir);

        var storagePath = Path.Combine(dir, filename);
        await using (var fs = new FileStream(storagePath, FileMode.Create, FileAccess.Write))
        {
            await content.CopyToAsync(fs);
        }

        var fileInfo = new FileInfo(storagePath);
        var entity = new WikiAttachmentEntity
        {
            PageId = pageId,
            Filename = filename,
            StoragePath = storagePath,
            ContentType = contentType,
            SizeBytes = fileInfo.Length,
            UploadedBy = username,
            CreatedAt = DateTime.UtcNow
        };

        entity = await store.CreateAttachmentAsync(entity);

        return new WikiAttachmentResponse(
            entity.Id, entity.Filename, entity.ContentType, entity.SizeBytes, entity.UploadedBy,
            $"/api/wiki/attachments/{entity.Id}/{entity.Filename}",
            entity.CreatedAt);
    }

    public async Task<(Stream Content, string ContentType, string Filename)?> GetAsync(long attachmentId)
    {
        var entity = await store.GetAttachmentByIdAsync(attachmentId);
        if (entity is null || !File.Exists(entity.StoragePath)) return null;

        var stream = new FileStream(entity.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (stream, entity.ContentType, entity.Filename);
    }

    public async Task<bool> DeleteAsync(long attachmentId)
    {
        var entity = await store.GetAttachmentByIdAsync(attachmentId);
        if (entity is null) return false;

        await store.DeleteAttachmentAsync(entity);

        // Best-effort file cleanup
        try
        {
            if (File.Exists(entity.StoragePath))
                File.Delete(entity.StoragePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete attachment file {Path}", entity.StoragePath);
        }

        return true;
    }
}
