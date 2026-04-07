using Shouldly;
using CodeGraph.Models;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Models;

namespace CodeGraph.Tests.Services;

public class AnalysisPromptBuilderTests
{
    [Fact]
    public void SystemPrompt_IsDomainAgnostic()
    {
        var prompt = AnalysisPromptBuilder.SystemPrompt;

        prompt.ShouldContain("Do not assume any specific business domain");
        prompt.ShouldNotContain("HugeDomains");
        prompt.ShouldNotContain("DropCatch");
        prompt.ShouldNotContain("NameBright");
        prompt.ShouldNotContain("domain name reseller");
    }

    [Fact]
    public void BuildProjectAnalysisPrompt_PrefersEvidenceOverInventedBusinessStory()
    {
        var prompt = AnalysisPromptBuilder.BuildProjectAnalysisPrompt(
            "CodeGraph.Api",
            "CodeGraph",
            "### Controller\n- CodeGraph.Api.Controllers.AdminController",
            [("Controllers/AdminController.cs", "public class AdminController : ControllerBase { }")]);

        prompt.ShouldContain("Base the summary on the evidence provided here.");
        prompt.ShouldContain("describe the technical responsibilities and note uncertainty");
        prompt.ShouldContain("\"projectName\": \"string\"");
    }

    [Fact]
    public void BuildRepoSynthesisPrompt_UsesRequestedSummaryProperty()
    {
        var prompt = AnalysisPromptBuilder.BuildRepoSynthesisPrompt(
            "CodeGraph",
            [
                new ProjectAnalysis(
                    "CodeGraph.Api",
                    "Hosts the API and background orchestration entrypoints.",
                    ConfidenceLevel.High,
                    [],
                    [],
                    [],
                    [])
            ],
            [
                new CrossRepoEdge
                {
                    SourceProject = "CodeGraph.Api",
                    TargetProject = "Shared.Contracts",
                    Type = EdgeType.REFERENCES_PACKAGE
                }
            ],
            summaryPropertyName: "repoSummary");

        prompt.ShouldContain("\"repoSummary\" : \"string\"");
        prompt.ShouldContain("CodeGraph.Api --REFERENCES_PACKAGE--> Shared.Contracts");
        prompt.ShouldContain("Stay close to the code evidence.");
    }
}
