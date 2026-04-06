using CodeGraph.Models.Messages;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services;

public interface IProjectService
{
    Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new());
    Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new());
    Task<bool> DeleteRepositoryAsync(string repo);
}
