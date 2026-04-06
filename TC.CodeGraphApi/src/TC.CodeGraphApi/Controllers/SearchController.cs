using Microsoft.AspNetCore.Mvc;
using TC.CodeGraphApi.Models.Responses;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Controllers;

[ApiController]
[Route("api/search")]
public class SearchController(ISearchService searchService) : Controller
{
    // GET /api/search?q=Orders&page=1&pageSize=25
    [HttpGet]
    public async Task<ActionResult<UnifiedSearchResponse>> Search(
        [FromQuery] string q = "",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        return Ok(await searchService.SearchAsync(q, page, pageSize));
    }
}
