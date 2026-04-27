using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Assistant;

public interface IAssistantConfigurationService
{
    Task<AssistantConfigurationResponse> GetConfigurationAsync(CancellationToken ct = default);
}
