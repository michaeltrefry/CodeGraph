using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services.Query;

public interface IGraphOverviewService
{
    Task<GraphOverviewResponse> GetOverviewAsync();
}
