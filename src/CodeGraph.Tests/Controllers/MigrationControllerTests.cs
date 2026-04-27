using CodeGraph.Api.Controllers;
using CodeGraph.Services.Migration;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class MigrationControllerTests
{
    [Fact]
    public async Task GetNeo4jToMariaDbDryRun_ReturnsPlannerReport()
    {
        var controller = new MigrationController(
            new Neo4jToMariaDbMigrationService(new Neo4jToMariaDbMigrationPlanner()));

        var result = await controller.GetNeo4jToMariaDbDryRun(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var report = ok.Value.ShouldBeOfType<Neo4jToMariaDbMigrationPlanReport>();
        report.DryRun.ShouldBeTrue();
        report.TotalAreas.ShouldBe(Neo4jToMariaDbMigrationManifest.Current.Areas.Count);
        report.CanExecute.ShouldBeFalse();
    }
}
