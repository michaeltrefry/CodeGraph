using TC.CodeGraphApi.Models.Messages;
using TC.CodeGraphApi.Models.Responses;

namespace TC.CodeGraphApi.Services;

public interface IProjectService
{
    Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new());
    Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new());
    Task<bool> DeleteRepositoryAsync(string repo);
}
