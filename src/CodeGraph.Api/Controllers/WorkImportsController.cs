using System.Text.Json;
using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using CodeGraph.Services.Work.Imports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

/// <summary>
/// The imports (sc-27876, design §10): settings (the team → project table the owner approves), dry
/// runs, imports and delta syncs as recorded runs with their mapping reports, imported attachments,
/// and the native-memory candidate parser that feeds S5's import_knowledge. Bodies and answers use
/// the work tools' JSON (snake_case, strict). Shortcut and KanbanBoard are only read.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = CodeGraphAuthenticationDefaults.Scheme + "," + McpPatAuthenticationDefaults.Scheme)]
[Route("api/work/imports")]
public sealed class WorkImportsController(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    ILogger<WorkImportsController> logger,
    WorkImportService? imports = null,
    IWorkImportStore? store = null,
    IWorkAttachmentStorage? files = null) : ControllerBase
{
    private string User => WorkService.ActorFor(base.User).Name;

    private WorkImportService Imports => imports ?? throw new WorkStoreUnavailableException();
    private IWorkImportStore Store => store ?? throw new WorkStoreUnavailableException();

    /// <summary>The saved settings of a source, or the proposal a dry run would use.</summary>
    [HttpGet("{source}/settings")]
    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    public Task<IActionResult> GetSettings(string source) =>
        Answer(async () => await Imports.GetSettingsAsync(source, HttpContext.RequestAborted));

    /// <summary>Saves the team → project table (and member map); with approve: true it is approved and imports may run.</summary>
    [HttpPut("{source}/settings")]
    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    public Task<IActionResult> SaveSettings(string source, [FromBody] JsonElement body) =>
        Answer(async () =>
        {
            var request = Read<ImportSettingsRequest>(body);
            var settings = new ImportSettings(request.HoldingProjectKey.Trim().ToUpperInvariant(),
                (request.Teams ?? []).Select(team => team with { ProjectKey = team.ProjectKey.Trim().ToUpperInvariant() }).ToList(),
                request.Members, request.KanbanEpicId);
            return await Imports.SaveSettingsAsync(source, settings, request.Approve == true, User, HttpContext.RequestAborted);
        });

    /// <summary>Starts a run (dry_run, import or delta) and answers 202 with its id; read the run for its status and report.</summary>
    [HttpPost("{source}/runs")]
    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    public Task<IActionResult> StartRun(string source, [FromBody] JsonElement body) =>
        Answer(async () =>
        {
            var request = body.ValueKind == JsonValueKind.Undefined ? new ImportRunRequest() : Read<ImportRunRequest>(body);
            var (runId, mode, since) = await Imports.StartAsync(source, request.Mode, request.UpdatedSince, User, HttpContext.RequestAborted);
            // A full Shortcut run takes about an hour at Shortcut's rate limit: it runs in the background,
            // in its own scope, until it finishes or the host stops.
            _ = Task.Run(async () =>
            {
                using var scope = scopes.CreateScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<WorkImportService>().ExecuteAsync(runId, source, mode, since, lifetime.ApplicationStopping);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Import run {RunId} stopped", runId);
                }
            });
            return new { run_id = runId, status = ImportRunStatuses.Running, mode, updated_since = since };
        }, StatusCodes.Status202Accepted);

    [HttpGet("{source}/runs")]
    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    public Task<IActionResult> ListRuns(string source, [FromQuery] int? limit) =>
        Answer(async () => await Store.ListImportRunsAsync(source, limit ?? 20, HttpContext.RequestAborted));

    /// <summary>A run with its mapping report.</summary>
    [HttpGet("runs/{runId:guid}")]
    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    public Task<IActionResult> GetRun(Guid runId) =>
        Answer(async () => await Store.GetImportRunAsync(runId, HttpContext.RequestAborted)
            ?? throw WorkException.NotFound($"There is no import run {runId}."));

    /// <summary>An attachment stored with an item (an imported Shortcut file).</summary>
    [HttpGet("/api/work/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> GetAttachment(Guid attachmentId)
    {
        try
        {
            var attachment = await Store.GetAttachmentAsync(attachmentId, HttpContext.RequestAborted);
            if (attachment?.StoragePath is not { } path || files is null)
                return Refuse(WorkException.NotFound($"Attachment {attachmentId} has no stored file."));
            return File(files.OpenRead(path), attachment.ContentType, attachment.Filename);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(exception, "Attachment {AttachmentId} could not be read", attachmentId);
            return Refuse(WorkException.NotFound($"Attachment {attachmentId} could not be read."));
        }
        catch (WorkStoreUnavailableException exception)
        {
            return Problem(detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Work tracking is unavailable");
        }
    }

    /// <summary>
    /// Parses native memory files into import_knowledge candidates with a path pre-check (design §10.3
    /// step 1). Stores nothing: the importing agent verifies the candidates, the owner accepts, edits
    /// or drops them, then import_knowledge runs its dry run and confirm.
    /// </summary>
    [HttpPost("memory/candidates")]
    public Task<IActionResult> MemoryCandidates([FromBody] JsonElement body) =>
        Answer(() =>
        {
            var request = Read<MemoryCandidatesRequest>(body);
            var root = request.RepositoryRoot is { } given && Directory.Exists(given) ? given : null;
            var files = request.Files.Select(file => new MemoryFile(file.Path, file.Content)).ToList();
            IReadOnlyList<MemoryCandidate> candidates = request.Harness.Trim().ToLowerInvariant() switch
            {
                "claude" => NativeMemoryParser.ParseClaude(files, root),
                "codex" => files.SelectMany(file => NativeMemoryParser.ParseCodex(file, root, request.CwdFilter)).ToList(),
                var other => throw WorkException.Invalid($"harness must be claude or codex; not '{other}'.")
            };
            return Task.FromResult<object>(new
            {
                candidates = candidates.Select(candidate => candidate.Candidate).ToList(),
                pre_check = candidates.Select((candidate, index) => new { index, candidate.Harness, memory_type = candidate.MemoryType, candidate.Referenced, candidate.Missing }).ToList()
            });
        });

    private static T Read<T>(JsonElement body)
    {
        try
        {
            return body.Deserialize<T>(WorkJson.Options) ?? throw WorkException.Invalid("The body must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw WorkException.Invalid($"The body is not valid: {exception.Message}");
        }
    }

    private async Task<IActionResult> Answer(Func<Task<object>> run, int status = StatusCodes.Status200OK)
    {
        try
        {
            var result = await run();
            return new ContentResult
            {
                Content = JsonSerializer.Serialize(result, result.GetType(), WorkJson.Options),
                ContentType = "application/json",
                StatusCode = status
            };
        }
        catch (WorkException exception)
        {
            return Refuse(exception);
        }
        catch (WorkStoreUnavailableException exception)
        {
            return Problem(detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Work tracking is unavailable");
        }
    }

    private static ContentResult Refuse(WorkException exception) => new()
    {
        Content = JsonSerializer.Serialize(new WorkError(exception.Code, exception.Message, exception.Current, exception.Reason), WorkJson.Options),
        ContentType = "application/json",
        StatusCode = WorkController.StatusFor(exception.Code)
    };
}
