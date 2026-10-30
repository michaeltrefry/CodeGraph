using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlDreamingStore(IOptions<MariaDbStorageOptions> options) : IDreamingStore
{
    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    { var c = new MySqlConnection(options.Value.ConnectionString); await c.OpenAsync(ct); return c; }
    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid projectId, CancellationToken ct = default)
    {
        var c = await OpenAsync(ct);
        var name = MySqlWorkStore.ServerLockName(c.Database, $"dream:{projectId}");
        try
        {
            if (await c.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT GET_LOCK(@name,0)", new { name }, cancellationToken: ct)) == 1)
                return new Lease(c, name);
            await c.DisposeAsync(); return null;
        }
        catch { await c.DisposeAsync(); throw; }
    }
    private sealed class Lease(MySqlConnection connection, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await connection.ExecuteAsync("DO RELEASE_LOCK(@name)", new { name }); }
            finally { await connection.DisposeAsync(); }
        }
    }
    public async Task<IReadOnlyList<Guid>> ListProjectIdsAsync(CancellationToken ct = default)
    { await using var c = await OpenAsync(ct); return (await c.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM work_projects ORDER BY project_key", cancellationToken: ct))).ToList(); }
    public async Task SaveRunAsync(DreamRun run, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO dreaming_runs(id,project_id,status,started_at,ended_at,run_json) VALUES(@Id,@ProjectId,@Status,@StartedAt,@EndedAt,@json) ON DUPLICATE KEY UPDATE status=VALUES(status),ended_at=VALUES(ended_at),run_json=VALUES(run_json)",
            new { run.Id, run.ProjectId, run.Status, run.StartedAt, run.EndedAt, json = JsonSerializer.Serialize(run, WorkJson.Options) }, cancellationToken: ct));
    }
    public async Task<DreamRun?> GetRunAsync(Guid projectId, Guid runId, CancellationToken ct = default)
    { await using var c = await OpenAsync(ct); var json = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT run_json FROM dreaming_runs WHERE project_id=@projectId AND id=@runId", new { projectId, runId }, cancellationToken: ct)); return json is null ? null : JsonSerializer.Deserialize<DreamRun>(json, WorkJson.Options); }
    public async Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit, CancellationToken ct = default)
    { await using var c = await OpenAsync(ct); return (await c.QueryAsync<string>(new CommandDefinition("SELECT run_json FROM dreaming_runs WHERE project_id=@projectId ORDER BY started_at DESC,id DESC LIMIT @limit", new { projectId, limit = Math.Clamp(limit, 1, 100) }, cancellationToken: ct))).Select(j => JsonSerializer.Deserialize<DreamRun>(j, WorkJson.Options)!).ToList(); }
    public async Task<IReadOnlyList<DreamRun>> ListRunsBeforeAsync(Guid projectId,Guid? before,int limit,CancellationToken ct=default)
    {
        await using var c=await OpenAsync(ct);
        DateTime? started=null;
        if(before is {} id)
            started=await c.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition("SELECT started_at FROM dreaming_runs WHERE id=@id AND project_id=@projectId",new {id,projectId},cancellationToken:ct))
                ??throw WorkException.Invalid("before must name a run in this project.");
        var rows=await c.QueryAsync<string>(new CommandDefinition("SELECT run_json FROM dreaming_runs WHERE project_id=@projectId AND (@before IS NULL OR started_at<@started OR (started_at=@started AND id<@before)) ORDER BY started_at DESC,id DESC LIMIT @limit",new {projectId,before,started,limit=Math.Clamp(limit,1,100)},cancellationToken:ct));
        return rows.Select(j=>JsonSerializer.Deserialize<DreamRun>(j,WorkJson.Options)!).ToList();
    }
    public async Task<DreamRun?> LastCompletedAsync(Guid projectId, CancellationToken ct = default)
    { await using var c = await OpenAsync(ct); var j = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT run_json FROM dreaming_runs WHERE project_id=@projectId AND status='completed' ORDER BY ended_at DESC LIMIT 1", new { projectId }, cancellationToken: ct)); return j is null ? null : JsonSerializer.Deserialize<DreamRun>(j, WorkJson.Options); }
    public async Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default)
    { await using var c = await OpenAsync(ct); var j = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT settings_json FROM dreaming_settings WHERE id=1", cancellationToken: ct)); return j is null ? new DreamSettings() : JsonSerializer.Deserialize<DreamSettings>(j, WorkJson.Options)!; }
    public async Task SetSettingsAsync(DreamSettings settings, WorkActor actor, CancellationToken ct = default)
    { DreamPolicy.Refuse(actor, "edit settings"); await using var c = await OpenAsync(ct); await c.ExecuteAsync(new CommandDefinition("INSERT INTO dreaming_settings(id,settings_json,updated_by,updated_at) VALUES(1,@json,@name,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE settings_json=VALUES(settings_json),updated_by=VALUES(updated_by),updated_at=VALUES(updated_at)", new { json = JsonSerializer.Serialize(settings, WorkJson.Options), name = actor.Name }, cancellationToken: ct)); }
    private static string Slug(Guid projectId) => $"dreaming-project-{projectId:N}";
    public async Task<DreamInstructionSet> GetInstructionsAsync(Guid projectId, CancellationToken ct = default)
    { await using var c = await OpenAsync(ct); return new(await ReadAsync(c,"dreaming-base",ct), await ReadAsync(c,Slug(projectId),ct)); }
    private static async Task<DreamInstructions> ReadAsync(MySqlConnection c, string slug, CancellationToken ct)
    {
        var page = await c.QuerySingleOrDefaultAsync<InstructionPage>(new CommandDefinition("SELECT id,content,revision FROM wiki_pages WHERE slug=@slug AND section_id=(SELECT id FROM wiki_sections WHERE slug='conventions') AND parent_id IS NULL LIMIT 1", new { slug }, cancellationToken: ct));
        if (page is null) return new(slug,"",0,[]);
        var history = await c.QueryAsync<DreamInstructionRevision>(new CommandDefinition("SELECT revision AS Revision,content AS Content,author AS EditedBy,created_at AS CreatedAt FROM wiki_revisions WHERE page_id=@id ORDER BY revision DESC", new { id = page.Id }, cancellationToken: ct));
        return new(slug,page.Content,page.Revision,history.ToList());
    }
    private sealed record InstructionPage(long Id,string Content,int Revision);
    public async Task<DreamInstructions> SetInstructionsAsync(Guid projectId, string scope, string content, int expectedRevision, WorkActor actor, CancellationToken ct = default)
    {
        DreamPolicy.Refuse(actor,"edit instructions");
        if (scope is not ("base" or "project") || content.Length > 100000) throw WorkException.Invalid("scope must be base or project; content is limited to 100000 characters.");
        var slug = scope == "base" ? "dreaming-base" : Slug(projectId);
        await using var c = await OpenAsync(ct);
        var name=MySqlWorkStore.ServerLockName(c.Database,$"dream-instructions:{slug}");
        if (await c.ExecuteScalarAsync<long>(new CommandDefinition("SELECT GET_LOCK(@name,10)",new {name},cancellationToken:ct)) != 1) throw WorkException.Busy("Instructions are being edited.");
        try
        {
            await using var tx=await c.BeginTransactionAsync(ct);
            var page=await c.QuerySingleOrDefaultAsync<InstructionPage>(new CommandDefinition("SELECT id,content,revision FROM wiki_pages WHERE slug=@slug AND section_id=(SELECT id FROM wiki_sections WHERE slug='conventions') AND parent_id IS NULL LIMIT 1 FOR UPDATE",new {slug},tx,cancellationToken:ct));
            if ((page?.Revision??0)!=expectedRevision) throw new WorkException(WorkErrorCodes.Conflict,"Instructions changed; reload before saving.");
            var revision=expectedRevision+1;
            long id;
            if(page is null)
                id=await c.ExecuteScalarAsync<long>(new CommandDefinition("INSERT INTO wiki_pages(section_id,parent_id,slug,title,content,author,revision) SELECT id,NULL,@slug,@slug,@content,@author,@revision FROM wiki_sections WHERE slug='conventions'; SELECT LAST_INSERT_ID();",new {slug,content,author=actor.Name,revision},tx,cancellationToken:ct));
            else { id=page.Id; await c.ExecuteAsync(new CommandDefinition("UPDATE wiki_pages SET content=@content,author=@author,revision=@revision WHERE id=@id",new {content,author=actor.Name,revision,id},tx,cancellationToken:ct)); }
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO wiki_revisions(page_id,revision,title,content,author) VALUES(@id,@revision,@slug,@content,@author)",new {id,revision,slug,content,author=actor.Name},tx,cancellationToken:ct));
            await tx.CommitAsync(ct); return await ReadAsync(c,slug,ct);
        }
        finally { await c.ExecuteAsync("DO RELEASE_LOCK(@name)",new {name}); }
    }
}
