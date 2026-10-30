using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed partial class MySqlDreamingStore(IOptions<MariaDbStorageOptions> options) : IDreamingStore
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
            {
                await RecoverInterruptedRunsAsync(c, projectId, ct);
                return new Lease(c, name);
            }
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
    { await using var c = await OpenAsync(ct); var json = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT run_json FROM dreaming_runs WHERE project_id=@projectId AND id=@runId", new { projectId, runId }, cancellationToken: ct)); return json is null ? null : await RecoverDurableActionsAsync(c, JsonSerializer.Deserialize<DreamRun>(json, WorkJson.Options)!, ct); }
    public async Task<IReadOnlyList<DreamRun>> ListRunsAsync(Guid projectId, int limit, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var runs = (await c.QueryAsync<string>(new CommandDefinition("SELECT run_json FROM dreaming_runs WHERE project_id=@projectId ORDER BY started_at DESC,id DESC LIMIT @limit", new { projectId, limit = Math.Clamp(limit, 1, 100) }, cancellationToken: ct))).Select(j => JsonSerializer.Deserialize<DreamRun>(j, WorkJson.Options)!).ToList();
        var recovered = new List<DreamRun>();
        foreach (var run in runs) recovered.Add(await RecoverDurableActionsAsync(c, run, ct));
        return recovered;
    }
    public async Task<IReadOnlyList<DreamRun>> ListRunsBeforeAsync(Guid projectId,Guid? before,int limit,CancellationToken ct=default)
    {
        await using var c=await OpenAsync(ct);
        DateTime? started=null;
        if(before is {} id)
            started=await c.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition("SELECT started_at FROM dreaming_runs WHERE id=@id AND project_id=@projectId",new {id,projectId},cancellationToken:ct))
                ??throw WorkException.Invalid("before must name a run in this project.");
        var rows=await c.QueryAsync<string>(new CommandDefinition("SELECT run_json FROM dreaming_runs WHERE project_id=@projectId AND (@before IS NULL OR started_at<@started OR (started_at=@started AND id<@before)) ORDER BY started_at DESC,id DESC LIMIT @limit",new {projectId,before,started,limit=Math.Clamp(limit,1,100)},cancellationToken:ct));
        var recovered = new List<DreamRun>();
        foreach (var json in rows) recovered.Add(await RecoverDurableActionsAsync(c, JsonSerializer.Deserialize<DreamRun>(json,WorkJson.Options)!,ct));
        return recovered;
    }
    public async Task<DreamRun?> LastCompletedAsync(Guid projectId, CancellationToken ct = default)
    { await using var c = await OpenAsync(ct); var j = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT run_json FROM dreaming_runs WHERE project_id=@projectId AND status='completed' ORDER BY ended_at DESC LIMIT 1", new { projectId }, cancellationToken: ct)); return j is null ? null : JsonSerializer.Deserialize<DreamRun>(j, WorkJson.Options); }
    public async Task<DreamSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var c=await OpenAsync(ct);
        await using var tx=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var row=await c.QuerySingleOrDefaultAsync<SettingsRow>(new CommandDefinition("SELECT settings_json AS Json,revision AS Revision FROM dreaming_settings WHERE id=1",transaction:tx,cancellationToken:ct));
        var schedule=await c.QuerySingleOrDefaultAsync<ScheduleUpdate>(new CommandDefinition("SELECT id AS Id,is_enabled AS Enabled,cron_expression AS Cron,time_zone_id AS Zone,schedule_revision AS Revision FROM job_schedules WHERE job_type='DreamProjects' ORDER BY id LIMIT 1",transaction:tx,cancellationToken:ct));
        var settings=WithVersion(row,schedule);
        await tx.CommitAsync(ct);return settings;
    }
    private sealed record SettingsRow(string Json,long Revision);
    private sealed record ScheduleUpdate(long Id,bool Enabled,string Cron,string Zone,long Revision);
    private static string SettingsVersion(SettingsRow? row,ScheduleUpdate? schedule)=>$"v1:{row?.Revision??0}:{schedule?.Id??0}:{schedule?.Revision??0}";
    private static DreamSettings WithVersion(SettingsRow? row,ScheduleUpdate? schedule)
    {
        var settings=row is null?new DreamSettings():JsonSerializer.Deserialize<DreamSettings>(row.Json,WorkJson.Options)!;
        return settings with {Enabled=schedule?.Enabled??settings.Enabled,Schedule=schedule?.Cron??settings.Schedule,Version=SettingsVersion(row,schedule)};
    }
    public async Task<DreamSettings> SetSettingsAsync(DreamSettings settings,WorkActor actor,CancellationToken ct=default)
    {
        DreamPolicy.Refuse(actor,"edit settings");
        if(string.IsNullOrWhiteSpace(settings.Version))throw WorkException.Invalid("Dreaming settings writes require the version returned by GetSettings.");
        Cronos.CronExpression cron;
        try{cron=Cronos.CronExpression.Parse(settings.Schedule,Cronos.CronFormat.Standard);}
        catch(FormatException){throw WorkException.Invalid("Dreaming schedule must be a standard five-field cron expression.");}
        await using var c=await OpenAsync(ct);
        var lockName=MySqlWorkStore.ServerLockName(c.Database,"dream-settings");
        if(await c.ExecuteScalarAsync<long>(new CommandDefinition("SELECT GET_LOCK(@lockName,10)",new {lockName},cancellationToken:ct))!=1)throw WorkException.Busy("Dreaming settings are being edited.");
        try
        {
            await using var tx=await c.BeginTransactionAsync(ct);
            var row=await c.QuerySingleOrDefaultAsync<SettingsRow>(new CommandDefinition("SELECT settings_json AS Json,revision AS Revision FROM dreaming_settings WHERE id=1 FOR UPDATE",transaction:tx,cancellationToken:ct));
            var schedule=await c.QuerySingleOrDefaultAsync<ScheduleUpdate>(new CommandDefinition("SELECT id AS Id,is_enabled AS Enabled,cron_expression AS Cron,time_zone_id AS Zone,schedule_revision AS Revision FROM job_schedules WHERE job_type='DreamProjects' ORDER BY id LIMIT 1 FOR UPDATE",transaction:tx,cancellationToken:ct));
            if(settings.Version!=SettingsVersion(row,schedule))throw new WorkException(WorkErrorCodes.Conflict,"Dreaming settings or their schedule changed; reload before saving.",WithVersion(row,schedule));
            var zone=TimeZoneInfo.FindSystemTimeZoneById(schedule?.Zone??"UTC");
            var next=cron.GetNextOccurrence(DateTime.UtcNow,zone,false)??throw WorkException.Invalid("Dreaming schedule has no future occurrence.");
            if(schedule is null)
                await c.ExecuteAsync(new CommandDefinition("INSERT INTO job_schedules(name,job_type,is_enabled,cron_expression,time_zone_id,args_json,next_run_utc) VALUES('dream-projects','DreamProjects',@enabled,@cron,'UTC','{}',@next)",new {enabled=settings.Enabled,cron=settings.Schedule,next},tx,cancellationToken:ct));
            else
                await c.ExecuteAsync(new CommandDefinition("UPDATE job_schedules SET next_run_utc=IF(cron_expression<>@cron OR is_enabled<>@enabled,@next,next_run_utc),is_enabled=@enabled,cron_expression=@cron,schedule_revision=schedule_revision+1 WHERE id=@id",new {enabled=settings.Enabled,cron=settings.Schedule,next,id=schedule.Id},tx,cancellationToken:ct));
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO dreaming_settings(id,settings_json,updated_by,updated_at,revision) VALUES(1,@json,@name,UTC_TIMESTAMP(6),1) ON DUPLICATE KEY UPDATE settings_json=VALUES(settings_json),updated_by=VALUES(updated_by),updated_at=VALUES(updated_at),revision=revision+1",new {json=JsonSerializer.Serialize(settings with {Version=null},WorkJson.Options),name=actor.Name},tx,cancellationToken:ct));
            var writtenRow=await c.QuerySingleAsync<SettingsRow>(new CommandDefinition("SELECT settings_json AS Json,revision AS Revision FROM dreaming_settings WHERE id=1",transaction:tx,cancellationToken:ct));
            var writtenSchedule=await c.QuerySingleAsync<ScheduleUpdate>(new CommandDefinition("SELECT id AS Id,is_enabled AS Enabled,cron_expression AS Cron,time_zone_id AS Zone,schedule_revision AS Revision FROM job_schedules WHERE job_type='DreamProjects' ORDER BY id LIMIT 1",transaction:tx,cancellationToken:ct));
            var result=WithVersion(writtenRow,writtenSchedule);
            await tx.CommitAsync(ct);return result;
        }
        finally{await c.ExecuteAsync("DO RELEASE_LOCK(@lockName)",new {lockName});}
    }
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
