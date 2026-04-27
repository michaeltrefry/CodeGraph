using CodeGraph.Api.Auth;
using CodeGraph.Data.Migration;
using CodeGraph.Services.Migration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
[Route("api/migration")]
public class MigrationController(INeo4jToMariaDbMigrationService migrationService) : ControllerBase
{
    [HttpGet("neo4j-to-mariadb/dry-run")]
    public async Task<ActionResult<Neo4jToMariaDbMigrationPlanReport>> GetNeo4jToMariaDbDryRun(CancellationToken ct)
        => Ok(await migrationService.CreateDryRunReportAsync(ct: ct));

    [HttpPost("neo4j-to-mariadb/repositories-graph/run")]
    public async Task<ActionResult<Neo4jToMariaDbGraphImportResult>> RunNeo4jToMariaDbRepositoriesGraph(CancellationToken ct)
    {
        var username = User.Identity?.Name;
        var result = await migrationService.RunRepositoriesAndGraphMigrationAsync(username, ct);
        return result.Status == "blocked"
            ? Conflict(result)
            : Ok(result);
    }
}
