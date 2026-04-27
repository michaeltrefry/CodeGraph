using Microsoft.AspNetCore.Mvc;
using CodeGraph.Models.Responses;
using CodeGraph.Services;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/schemas")]
public class SchemasController(IProjectQueryService queryService) : Controller
{
    [HttpGet]
    public async Task<ActionResult<SchemaListResponse>> List(
        [FromQuery] string? search,
        [FromQuery] string? server,
        [FromQuery] string? database,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        return Ok(await queryService.ListSchemasAsync(search, server, database, page, pageSize));
    }

    [HttpGet("{name}/catalog")]
    public async Task<ActionResult<SchemaCatalogResponse>> Catalog(string name)
    {
        var result = await queryService.GetSchemaCatalogAsync(name);
        return result is null ? NotFound() : Ok(result);
    }
}
