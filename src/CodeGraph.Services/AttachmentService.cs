using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services;

public class AttachmentService(
    IWikiStore store,
    IOptions<WikiOptions> wikiOptionsAccessor,
    ILogger<AttachmentService> logger) : IAttachmentService, IDisposable
{
    private readonly AttachmentStorage attachmentStorage = new(wikiOptionsAccessor.Value.AttachmentStoragePath);

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
        var storedFile = await attachmentStorage.CreateAsync(pageId, content);
        var entity = new WikiAttachmentEntity
        {
            PageId = pageId,
            Filename = displayName,
            StoragePath = storedFile.Path,
            ContentType = contentType,
            SizeBytes = storedFile.Size,
            UploadedBy = username,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            entity = await store.CreateAttachmentAsync(entity);
        }
        catch
        {
            try
            {
                attachmentStorage.DeleteCreated(storedFile.Path);
            }
            catch (Exception cleanupException)
            {
                logger.LogError(cleanupException, "Failed to roll back attachment file {Path}", storedFile.Path);
            }
            throw;
        }

        return ToResponse(entity);
    }

    public async Task<(Stream Content, string ContentType, string Filename)?> GetAsync(long attachmentId)
    {
        var entity = await store.GetAttachmentByIdAsync(attachmentId);
        if (entity is null) return null;

        try
        {
            var stream = attachmentStorage.OpenRead(entity.StoragePath);
            return (stream, entity.ContentType, entity.Filename);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Rejected or unavailable attachment storage path {Path}", entity.StoragePath);
            return null;
        }
    }

    public async Task<bool> DeleteAsync(long attachmentId)
    {
        var entity = await store.GetAttachmentByIdAsync(attachmentId);
        if (entity is null) return false;

        AttachmentDeletionLease deletion;
        try
        {
            deletion = attachmentStorage.Quarantine(entity.StoragePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rejected or failed to quarantine attachment file {Path}", entity.StoragePath);
            return false;
        }

        using (deletion)
        {
            try
            {
                await store.DeleteAttachmentAsync(entity);
            }
            catch
            {
                try
                {
                    deletion.Rollback();
                }
                catch (Exception compensationException)
                {
                    logger.LogCritical(
                        compensationException,
                        "Failed to restore attachment {AttachmentId} at {Path} after metadata deletion failed",
                        entity.Id,
                        entity.StoragePath);
                }
                throw;
            }

            try
            {
                deletion.Commit();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to finalize deletion of attachment file {Path}", entity.StoragePath);
                try
                {
                    deletion.Rollback();
                    await store.CreateAttachmentAsync(entity);
                }
                catch (Exception compensationException)
                {
                    logger.LogCritical(
                        compensationException,
                        "Failed to compensate attachment metadata after file deletion failed for {Path}",
                        entity.StoragePath);
                }
                return false;
            }
        }
    }

    private WikiAttachmentResponse ToResponse(WikiAttachmentEntity entity) => new(
        entity.Id,
        entity.Filename,
        entity.ContentType,
        entity.SizeBytes,
        entity.UploadedBy,
        $"/api/wiki/attachments/{entity.Id}/{Uri.EscapeDataString(entity.Filename)}",
        entity.CreatedAt);

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

    public void Dispose() => attachmentStorage.Dispose();

}
