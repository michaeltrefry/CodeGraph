using CodeGraph.Models.Responses;

namespace CodeGraph.Data;

public interface IDbHealthStore
{
    Task<DatabaseHealthResponse> GetDatabaseHealthAsync();
}
