using Microsoft.AspNetCore.Mvc;
using CodeGraph.Api.Auth;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/wiki")]
public class WikiController(
    IWikiService wikiService,
    IAttachmentService attachmentService,
    WikiOptions wikiOptions) : Controller
{
    // ── Sections ──

    [HttpGet("sections")]
    public async Task<ActionResult<IReadOnlyList<WikiSectionResponse>>> ListSections()
    {
        return Ok(await wikiService.ListSectionsAsync());
    }

    [HttpGet("{section}/tree")]
    public async Task<ActionResult<List<WikiTreeNode>>> GetSectionTree(string section)
    {
        return Ok(await wikiService.GetSectionTreeAsync(section));
    }

    // ── Pages ──

    [HttpGet("{section}/{**path}")]
    public async Task<ActionResult> GetPage(string section, string path)
    {
        // Check if this is a sub-route (revisions, attachments)
        if (path.EndsWith("/revisions"))
        {
            var pagePath = path[..^"/revisions".Length];
            return Ok(await wikiService.GetRevisionsAsync(section, pagePath));
        }

        if (path.Contains("/revisions/"))
        {
            var parts = path.Split("/revisions/");
            if (int.TryParse(parts[1], out var rev))
            {
                var result = await wikiService.GetRevisionAsync(section, parts[0], rev);
                return result is null ? NotFound() : Ok(result);
            }
        }

        if (path.EndsWith("/attachments"))
        {
            var pagePath = path[..^"/attachments".Length];
            var page = await wikiService.GetPageAsync(section, pagePath);
            if (page is null) return NotFound();
            return Ok(await attachmentService.ListAsync(page.Id));
        }

        var pageResult = await wikiService.GetPageAsync(section, path);
        return pageResult is null ? NotFound() : Ok(pageResult);
    }

    [HttpPost("{section}")]
    public async Task<ActionResult<WikiPageListItem>> CreatePage(string section, [FromBody] WikiPageRequest request)
    {
        var username = request.Author ?? User.GetUsername() ?? "anonymous";

        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Title and content are required.");

        var result = await wikiService.CreatePageAsync(section, request, username);
        if (result is null)
            return Conflict("Page already exists or section does not allow user pages.");

        return CreatedAtAction(nameof(GetPage), new { section, path = result.Slug }, result);
    }

    [HttpPost("{section}/{**path}")]
    public async Task<ActionResult> CreateOrUpload(string section, string path)
    {
        // Attachment upload
        if (path.EndsWith("/attachments"))
        {
            var username = Request.Headers["X-Author"].FirstOrDefault() ?? User.GetUsername() ?? "anonymous";
            var pagePath = path[..^"/attachments".Length];
            var page = await wikiService.GetPageAsync(section, pagePath);
            if (page is null) return NotFound();

            if (!Request.HasFormContentType || Request.Form.Files.Count == 0)
                return BadRequest("No file uploaded.");

            var file = Request.Form.Files[0];
            var maxBytes = wikiOptions.MaxAttachmentSizeMb * 1024L * 1024L;
            if (file.Length > maxBytes)
                return BadRequest($"File exceeds {wikiOptions.MaxAttachmentSizeMb}MB limit.");

            using var stream = file.OpenReadStream();
            var attachment = await attachmentService.UploadAsync(
                page.Id, file.FileName, file.ContentType, stream, username);

            return attachment is null ? NotFound() : Ok(attachment);
        }

        // Child page creation
        if (string.IsNullOrWhiteSpace(Request.ContentType) || !Request.ContentType.Contains("json"))
            return BadRequest("Expected JSON body for page creation.");

        var request = await System.Text.Json.JsonSerializer.DeserializeAsync<WikiPageRequest>(
            Request.Body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (request is null || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Title and content are required.");

        var childUsername = request.Author ?? User.GetUsername() ?? "anonymous";
        var result = await wikiService.CreateChildPageAsync(section, path, request, childUsername);
        if (result is null)
            return Conflict("Page already exists, max depth exceeded, or section does not allow user pages.");

        return Created($"/api/wiki/{section}/{path}/{result.Slug}", result);
    }

    [HttpPut("{section}/{**path}")]
    public async Task<ActionResult<WikiPageListItem>> UpdatePage(string section, string path, [FromBody] WikiPageRequest request)
    {
        var username = request.Author ?? User.GetUsername() ?? "anonymous";

        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Title and content are required.");

        var result = await wikiService.UpdatePageAsync(section, path, request, username);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("{section}/{**path}")]
    public async Task<ActionResult> DeletePage(string section, string path)
    {
        return await wikiService.DeletePageAsync(section, path) ? NoContent() : NotFound();
    }

    [HttpPatch("{section}/{**path}")]
    public async Task<ActionResult> MovePage(string section, string path, [FromBody] WikiPageMoveRequest request)
    {
        // Only handle /move suffix
        if (!path.EndsWith("/move"))
            return BadRequest("Use PATCH with /move suffix.");

        var pagePath = path[..^"/move".Length];
        return await wikiService.MovePageAsync(section, pagePath, request) ? Ok() : NotFound();
    }

    // ── Attachments (direct access) ──

    [HttpGet("attachments/{id:long}/{filename}")]
    public async Task<ActionResult> DownloadAttachment(long id, string filename)
    {
        var result = await attachmentService.GetAsync(id);
        if (result is null) return NotFound();

        var (content, contentType, storedFilename) = result.Value;
        return File(content, contentType, storedFilename);
    }

    [HttpDelete("attachments/{id:long}")]
    public async Task<ActionResult> DeleteAttachment(long id)
    {
        return await attachmentService.DeleteAsync(id) ? NoContent() : NotFound();
    }
}
