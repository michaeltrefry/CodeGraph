using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Query;

public interface IGraphOverviewService
{
    Task<GraphOverviewResponse> GetOverviewAsync();
}
