using System.Text.Json;
using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using CodeGraph.Services.Work.Imports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CodeGraph.Api.Controllers;

/// <summary>
/// The import administration (sc-27876, design §10): settings (the team → project table the owner
/// approves), dry runs, imports and delta syncs as recorded runs with their mapping reports. Admins
/// only, signed in through the web scheme: a personal access token is refused, because approving the
/// table and starting an import are the human decisions of the migration. Shortcut and KanbanBoard are
/// only read. Bodies and answers use the work tools' JSON (snake_case, strict).
/// </summary>
[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
[Route("api/work/imports")]
public sealed class WorkImportsAdminController(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    ILogger<WorkImportsAdminController> logger,
    WorkImportService? imports = null,
    IWorkImportStore? store = null) : ControllerBase
{
    /// <summary>Said with every settings answer: the delta schedule is the owner's to create, and to remove.</summary>
    public const string ScheduleNote =
        "No WorkImport schedule is seeded: it needs the source token (MCP Hub credentials) and approved settings first. After the full import, " +
        "create a WorkImport schedule (no args = Shortcut delta sync) under Admin > Schedules for the delta syncs until cutover, and remove it at cutover.";

    private string User => WorkService.ActorFor(base.User).Name;

    private WorkImportService Imports => imports ?? throw new WorkStoreUnavailableException();
    private IWorkImportStore Store => store ?? throw new WorkStoreUnavailableException();

    /// <summary>The saved settings of a source, or the proposal a dry run would use.</summary>
    [HttpGet("{source}/settings")]
    public Task<IActionResult> GetSettings(string source) =>
        WorkImportsHttp.Answer(this, async () => new { settings = await Imports.GetSettingsAsync(source, HttpContext.RequestAborted), schedule = ScheduleNote });

    /// <summary>Saves the team → project table (and member map); with approve: true it is approved and imports may run.</summary>
    [HttpPut("{source}/settings")]
    public Task<IActionResult> SaveSettings(string source, [FromBody] JsonElement body) =>
        WorkImportsHttp.Answer(this, async () =>
        {
            var request = WorkImportsHttp.Read<ImportSettingsRequest>(body);
            var settings = new ImportSettings(request.HoldingProjectKey.Trim().ToUpperInvariant(),
                (request.Teams ?? []).Select(team => team with { ProjectKey = team.ProjectKey.Trim().ToUpperInvariant() }).ToList(),
                request.Members, request.KanbanEpicId, RepositoryAliases: request.RepositoryAliases);
            return new { settings = await Imports.SaveSettingsAsync(source, settings, request.Approve == true, User, HttpContext.RequestAborted), schedule = ScheduleNote };
        });

    /// <summary>Starts a run (dry_run, import or delta) and answers 202 with its id; read the run for its status and report.</summary>
    [HttpPost("{source}/runs")]
    public Task<IActionResult> StartRun(string source, [FromBody] JsonElement body) =>
        WorkImportsHttp.Answer(this, async () =>
        {
            var request = body.ValueKind == JsonValueKind.Undefined ? new ImportRunRequest() : WorkImportsHttp.Read<ImportRunRequest>(body);
            var run = await Imports.StartAsync(source, request.Mode, request.UpdatedSince, User, HttpContext.RequestAborted);
            // A full Shortcut run takes about an hour at Shortcut's rate limit: it runs in the background,
            // in its own scope, until it finishes or the host stops.
            _ = Task.Run(async () =>
            {
                using var scope = scopes.CreateScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<WorkImportService>().ExecuteAsync(run, lifetime.ApplicationStopping);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Import run {RunId} stopped", run.RunId);
                }
            });
            return new { run_id = run.RunId, status = ImportRunStatuses.Running, mode = run.Mode, updated_since = run.UpdatedSince, retrying = run.Retry };
        }, StatusCodes.Status202Accepted);

    [HttpGet("{source}/runs")]
    public Task<IActionResult> ListRuns(string source, [FromQuery] int? limit) =>
        WorkImportsHttp.Answer(this, async () => await Store.ListImportRunsAsync(source, limit ?? 20, HttpContext.RequestAborted));

    /// <summary>A run with its mapping report.</summary>
    [HttpGet("runs/{runId:guid}")]
    public Task<IActionResult> GetRun(Guid runId) =>
        WorkImportsHttp.Answer(this, async () => await Store.GetImportRunAsync(runId, HttpContext.RequestAborted)
            ?? throw WorkException.NotFound($"There is no import run {runId}."));
}

/// <summary>
/// What agents use of the imports, with a personal access token or a sign-in: the files stored with
/// imported items, and the native-memory candidate parser of the verify-before-import flow (D18, design
/// §10.3), which stores nothing.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = CodeGraphAuthenticationDefaults.Scheme + "," + McpPatAuthenticationDefaults.Scheme)]
public sealed class WorkImportsController(
    ILogger<WorkImportsController> logger,
    IOptions<RepositorySourceOptions> repositorySource,
    IWorkImportStore? store = null,
    IWorkAttachmentStorage? files = null,
    IGraphStore? graph = null) : ControllerBase
{
    /// <summary>An attachment stored with an item (an imported Shortcut file).</summary>
    [HttpGet("api/work/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> GetAttachment(Guid attachmentId)
    {
        try
        {
            if (store is null)
                throw new WorkStoreUnavailableException();
            var attachment = await store.GetAttachmentAsync(attachmentId, HttpContext.RequestAborted);
            if (attachment?.StoragePath is not { } path || files is null)
                return WorkImportsHttp.Refuse(WorkException.NotFound($"Attachment {attachmentId} has no stored file."));
            return File(files.OpenRead(path), attachment.ContentType, attachment.Filename);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(exception, "Attachment {AttachmentId} could not be read", attachmentId);
            return WorkImportsHttp.Refuse(WorkException.NotFound($"Attachment {attachmentId} could not be read."));
        }
        catch (WorkStoreUnavailableException exception)
        {
            return Problem(detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Work tracking is unavailable");
        }
    }

    /// <summary>
    /// Parses native memory files into import_knowledge candidates with a path pre-check against the
    /// checkout of a repository CodeGraph knows (design §10.3 step 1). Stores nothing: the importing
    /// agent verifies the candidates, the owner accepts, edits or drops them, then import_knowledge runs
    /// its dry run and confirm.
    /// </summary>
    [HttpPost("api/work/imports/memory/candidates")]
    public Task<IActionResult> MemoryCandidates([FromBody] JsonElement body) =>
        WorkImportsHttp.Answer(this, async () =>
        {
            var request = WorkImportsHttp.Read<MemoryCandidatesRequest>(body);
            if (request.Files is null)
                throw WorkException.Invalid("files is required: the memory files as {path, content}.");
            var checkout = request.Repository is { } name ? await CheckoutAsync(name) : null;
            var files = request.Files.Select(file => new MemoryFile(file.Path, file.Content)).ToList();
            IReadOnlyList<MemoryCandidate> candidates = request.Harness.Trim().ToLowerInvariant() switch
            {
                "claude" => NativeMemoryParser.ParseClaude(files, checkout),
                "codex" => files.SelectMany(file => NativeMemoryParser.ParseCodex(file, checkout, request.CwdFilter)).ToList(),
                var other => throw WorkException.Invalid($"harness must be claude or codex; not '{other}'.")
            };
            return new
            {
                candidates = candidates.Select(candidate => candidate.Candidate).ToList(),
                pre_check = candidates.Select((candidate, index) => new { index, candidate.Harness, memory_type = candidate.MemoryType, candidate.Referenced, candidate.Missing }).ToList(),
                pre_check_truncated = checkout?.Truncated == true ? true : (bool?)null
            };
        });

    /// <summary>The checkout of an indexed repository, resolved the way source reads resolve it (cache, then indexed path).</summary>
    private async Task<RepositoryCheckout> CheckoutAsync(string name)
    {
        var repository = graph is null ? null : await graph.GetRepositoryByName(name.Trim());
        var root = repository is null ? null : RepoFileResolver.ResolveRepositoryRoot(repository.Name, repositorySource.Value.ReposCachePath, repository.LocalPath);
        if (root is null)
            throw WorkException.Invalid($"'{name}' is not a repository CodeGraph has a checkout of; pass the name of an indexed repository, or leave repository out.");
        return new RepositoryCheckout(root);
    }
}

internal static class WorkImportsHttp
{
    public static T Read<T>(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw WorkException.Invalid("The body must be a JSON object.");
        try
        {
            return body.Deserialize<T>(WorkJson.Options) ?? throw WorkException.Invalid("The body must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw WorkException.Invalid($"The body is not valid: {exception.Message}");
        }
    }

    public static async Task<IActionResult> Answer(ControllerBase controller, Func<Task<object>> run, int status = StatusCodes.Status200OK)
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
            return controller.Problem(detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Work tracking is unavailable");
        }
    }

    public static ContentResult Refuse(WorkException exception) => new()
    {
        Content = JsonSerializer.Serialize(new WorkError(exception.Code, exception.Message, exception.Current, exception.Reason), WorkJson.Options),
        ContentType = "application/json",
        StatusCode = WorkController.StatusFor(exception.Code)
    };
}
