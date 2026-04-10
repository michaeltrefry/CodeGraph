using CodeGraph.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Api;

public class MemoryControllerRouteTests
{
    [Fact]
    public void GetClaimBundle_ExposesCanonicalAndCompatibilityRoutes()
    {
        var templates = typeof(MemoryController)
            .GetMethod(nameof(MemoryController.GetClaimBundle))!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Select(attribute => attribute.Template)
            .Where(template => template != null)
            .ToList();

        templates.ShouldContain("claims/{id}");
        templates.ShouldContain("claims/{id}/bundle");
    }
}
