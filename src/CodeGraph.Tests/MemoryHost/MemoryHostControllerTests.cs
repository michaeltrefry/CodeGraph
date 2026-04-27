using CodeGraph.Memory.Host.Controllers;
using CodeGraph.Models.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.MemoryHost;

public class MemoryHostControllerTests
{
    [Fact]
    public async Task StoreClaims_RejectsEmptyExtraction()
    {
        var controller = CreateController();

        var result = await controller.StoreClaims(new MemoryClaimExtractionResult(), source: "test");

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CleanupBySource_RejectsBlankSource()
    {
        var controller = CreateController();

        var result = await controller.DeleteBySource(new MemoryCleanupBySourceRequest { Source = " " });

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CleanupByIds_RequiresClaimOrEntityId()
    {
        var controller = CreateController();

        var result = await controller.DeleteByIds(new MemoryCleanupByIdsRequest());

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ExpandFrontier_RequiresFrontierSeed()
    {
        var controller = CreateController();

        var result = await controller.ExpandFrontier(new MemoryFrontierExpansionRequest());

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RenderSummary_RequiresEntityOrClaimId()
    {
        var controller = CreateController();

        var result = await controller.RenderSummary(new MemorySummaryRenderRequest());

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void StoreClaims_ExposesCanonicalAndCompatibilityRoutes()
    {
        var templates = typeof(MemoryController)
            .GetMethod(nameof(MemoryController.StoreClaims))!
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Select(attribute => attribute.Template)
            .Where(template => template != null)
            .ToList();

        templates.ShouldContain("claims/store");
        templates.ShouldContain("store");
    }

    private static MemoryController CreateController()
    {
        return new MemoryController(null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
