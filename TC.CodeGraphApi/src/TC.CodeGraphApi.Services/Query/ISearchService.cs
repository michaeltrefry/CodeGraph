using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services.Query;

public interface ISearchService
{
    Task<UnifiedSearchResponse> SearchAsync(string query, int page = 1, int pageSize = 25);
}
