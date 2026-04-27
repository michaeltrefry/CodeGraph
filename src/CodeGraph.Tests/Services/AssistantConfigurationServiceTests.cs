using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class AssistantConfigurationServiceTests
{
    [Fact]
    public async Task GetConfigurationAsync_ReturnsAssistantAndIndexingDefaults()
    {
        var service = new AssistantConfigurationService(Options.Create(new AnalysisOptions
        {
            DefaultProvider = "openai",
            Model = "gpt-5",
            Assistant =
            {
                Provider = "lmstudio",
                Model = "qwen3"
            },
            LmStudio =
            {
                Model = "qwen3"
            }
        }));

        var response = await service.GetConfigurationAsync();

        response.DefaultProvider.ShouldBe("lmstudio");
        response.DefaultModel.ShouldBe("qwen3");
        response.Indexing.Provider.ShouldBe("openai");
        response.Indexing.Model.ShouldBe("gpt-5");
        response.Providers.ShouldContain(provider => provider.Name == "lmstudio" && provider.Models.Contains("qwen3"));
    }
}
