using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/admin/logs")]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
public sealed class AdminLogsController(IApplicationLogStore store) : ControllerBase
{
    private const int PageSize = 100;
    private const int MaxPage = 1_000_000;
    private const int MaxSearchLength = 256;
    private static readonly HashSet<string> SupportedLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Trace",
        "Debug",
        "Information",
        "Warning",
        "Error",
        "Critical"
    };

    [HttpGet]
    public async Task<ActionResult<ApplicationLogPageResponse>> List(
        [FromQuery] AdminApplicationLogQueryRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Page < 1)
            return BadRequest("Page must be at least 1.");
        if (request.Page > MaxPage)
            return BadRequest($"Page cannot exceed {MaxPage}.");

        var requestedContainer = NormalizeOptional(request.Container);
        var service = ApplicationLogServices.Normalize(requestedContainer);
        if (requestedContainer is not null && service is null)
            return BadRequest("Container must be api, indexer, jobs, memory, or metrics.");

        var level = NormalizeOptional(request.Level);
        if (level is not null && !SupportedLevels.Contains(level))
            return BadRequest("Level must be Trace, Debug, Information, Warning, Error, or Critical.");

        var search = NormalizeOptional(request.Search);
        if (search?.Length > MaxSearchLength)
            return BadRequest($"Search cannot exceed {MaxSearchLength} characters.");

        var startUtc = request.Start?.UtcDateTime;
        var endUtc = request.End?.UtcDateTime;
        if (startUtc.HasValue && endUtc.HasValue && startUtc > endUtc)
            return BadRequest("Start must be before or equal to end.");

        var result = await store.QueryAsync(
            new ApplicationLogQuery(request.Page, PageSize, service, level, startUtc, endUtc, search),
            cancellationToken);
        var totalPages = result.TotalCount == 0
            ? 0
            : (int)Math.Ceiling(result.TotalCount / (double)PageSize);

        return Ok(new ApplicationLogPageResponse(
            result.Entries.Select(Map).ToList(),
            request.Page,
            PageSize,
            result.TotalCount,
            totalPages));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ApplicationLogEntryResponse Map(ApplicationLogEntryEntity row) => new(
        row.Id,
        DateTime.SpecifyKind(row.OccurredAtUtc, DateTimeKind.Utc),
        row.Service,
        row.Level,
        row.Source,
        row.Category,
        row.EventId,
        row.Message,
        row.Exception,
        row.TraceId,
        row.SpanId,
        row.PropertiesJson);
}
