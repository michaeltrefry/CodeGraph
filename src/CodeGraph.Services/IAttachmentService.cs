using CodeGraph.Models.Responses;

namespace CodeGraph.Services;

public interface IAttachmentService
{
    Task<IReadOnlyList<WikiAttachmentResponse>> ListAsync(long pageId);
    Task<WikiAttachmentResponse?> UploadAsync(long pageId, string filename, string contentType, Stream content, string username);
    Task<(Stream Content, string ContentType, string Filename)?> GetAsync(long attachmentId);
    Task<bool> DeleteAsync(long attachmentId);
}
