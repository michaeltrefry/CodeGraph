using TC.CodeGraphApi.Models.Requests;
using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services;

public interface IWikiService
{
    // Sections
    Task<IReadOnlyList<WikiSectionResponse>> ListSectionsAsync();
    Task<WikiSectionResponse?> GetSectionAsync(string sectionSlug);
    Task<WikiSectionResponse?> CreateSectionAsync(WikiSectionRequest request);
    Task<WikiSectionResponse?> UpdateSectionAsync(long id, WikiSectionRequest request);
    Task<bool> DeleteSectionAsync(long id);

    // Tree
    Task<List<WikiTreeNode>> GetSectionTreeAsync(string sectionSlug);

    // Pages
    Task<WikiPageResponse?> GetPageAsync(string sectionSlug, string path);
    Task<WikiPageListItem?> CreatePageAsync(string sectionSlug, WikiPageRequest request, string author);
    Task<WikiPageListItem?> CreateChildPageAsync(string sectionSlug, string parentPath, WikiPageRequest request, string author);
    Task<WikiPageListItem?> UpdatePageAsync(string sectionSlug, string path, WikiPageRequest request, string author);
    Task<bool> DeletePageAsync(string sectionSlug, string path);
    Task<bool> MovePageAsync(string sectionSlug, string path, WikiPageMoveRequest request);

    // Revisions
    Task<IReadOnlyList<WikiRevisionListItem>> GetRevisionsAsync(string sectionSlug, string path);
    Task<WikiRevisionResponse?> GetRevisionAsync(string sectionSlug, string path, int revision);
}
