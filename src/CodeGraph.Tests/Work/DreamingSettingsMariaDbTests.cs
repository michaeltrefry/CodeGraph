using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed class DreamingSettingsMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly WorkActor Human=new("alice");
    private MySqlDreamingStore Store()=>new(Options.Create(new MariaDbStorageOptions {ConnectionString=db.ConnectionString}));

    [Fact]
    public async Task Two_clients_cannot_silently_reenable_settings_from_a_stale_budget_form()
    {
        var store=Store();
        var a=await store.GetSettingsAsync();var b=await store.GetSettingsAsync();
        var saved=await store.SetSettingsAsync(a with {Enabled=false},Human);
        saved.Version.ShouldNotBe(a.Version);saved.Enabled.ShouldBeFalse();
        var before=JsonSerializer.Serialize(saved,WorkJson.Options);
        var error=await Should.ThrowAsync<WorkException>(()=>store.SetSettingsAsync(b with {Enabled=true,MaxTokensPerRun=b.MaxTokensPerRun+1000},Human));
        error.Code.ShouldBe(WorkErrorCodes.Conflict);
        JsonSerializer.Serialize(await store.GetSettingsAsync(),WorkJson.Options).ShouldBe(before);
        await using var connection=new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<bool>("SELECT is_enabled FROM job_schedules WHERE job_type='DreamProjects' LIMIT 1")).ShouldBeFalse();
    }

    [Fact]
    public async Task An_authoritative_admin_schedule_edit_invalidates_the_settings_version()
    {
        var store=Store();var current=await store.GetSettingsAsync();
        current=await store.SetSettingsAsync(current with {Enabled=true},Human);
        await using var context=new CodeGraphDbContext(new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(db.ConnectionString,ServerVersion.Create(new Version(11,4,0),ServerType.MariaDb)).Options);
        var schedules=new MySqlJobScheduleStore(context);
        var schedule=(await schedules.ListSchedulesAsync()).Single(s=>s.JobType=="DreamProjects");
        schedule.CronExpression="15 4 * * *";schedule.IsEnabled=false;
        (await schedules.UpdateScheduleAsync(schedule)).ShouldBeTrue();
        var changed=await store.GetSettingsAsync();
        changed.Version.ShouldNotBe(current.Version);changed.Schedule.ShouldBe("15 4 * * *");changed.Enabled.ShouldBeFalse();
        var error=await Should.ThrowAsync<WorkException>(()=>store.SetSettingsAsync(current with {MaxTurns=current.MaxTurns+1},Human));
        error.Code.ShouldBe(WorkErrorCodes.Conflict);
        (await store.GetSettingsAsync()).ShouldBe(changed);
    }

    [Fact]
    public async Task Settings_instructions_runs_and_stale_saves_use_the_same_rest_and_mcp_service_contract()
    {
        var store=Store();var model=new NoCallsModel();
        using var services=new ServiceCollection().AddSingleton<IWorkStore>(db.Store).AddSingleton<IDreamingStore>(store)
            .AddSingleton<IKnowledgeDreamActions>(db.Service).AddSingleton<IDreamBatchModel>(model).AddSingleton<IDreamSourceReader>(new NoSourceReads())
            .AddSingleton<IDreamingService,DreamingService>().AddSingleton<IWorkService>(sp=>new WorkService(db.Store,db.Clock,serviceProvider:sp)).BuildServiceProvider();
        var work=services.GetRequiredService<IWorkService>();
        await using var host=await WorkRestHost.StartAsync(work,db.Store);
        var token=await host.CreateTokenAsync("alice",WorkTools.All.Select(tool=>tool.Name).ToArray());
        var key=await db.NewProjectAsync("DS","codegraph");
        var path=$"/api/work/dreaming/{key}";
        async Task<(int Status,JsonElement Body)> Call(HttpMethod method,string suffix,object? body=null)=>
            await host.SendAsync(method,path+suffix,body is null?null:JsonSerializer.Serialize(body,WorkJson.Options),token);
        var (readStatus,read)=await Call(HttpMethod.Get,"/settings");readStatus.ShouldBe(200);
        var current=read.Deserialize<DreamSettings>(WorkJson.Options)!;
        var (saveStatus,saved)=await Call(HttpMethod.Put,"/settings",new {settings=current with {Enabled=false},expected_version=current.Version});
        saveStatus.ShouldBe(200);saved.GetProperty("version").GetString().ShouldNotBe(current.Version);
        (await Call(HttpMethod.Put,"/settings",new {settings=current,expected_version=current.Version})).Status.ShouldBe(409);
        (await Call(HttpMethod.Put,"/settings",new {settings=current})).Status.ShouldBe(400);
        var toolError=await Should.ThrowAsync<WorkException>(()=>work.ExecuteAsync(WorkTools.DreamSettings,
            JsonSerializer.SerializeToElement(new {settings=current,expected_version=current.Version},WorkJson.Options),Human));
        toolError.Code.ShouldBe(WorkErrorCodes.Conflict);
        WorkTools.Find(WorkTools.DreamSettings)!.ArgumentsType.ShouldBe(typeof(DreamSettingsRequest));
        WorkJson.SchemaOf(typeof(DreamSettingsRequest)).GetRawText().ShouldContain("expected_version");
        var (runStatus,run)=await Call(HttpMethod.Post,"/runs",new {});runStatus.ShouldBe(200);
        run.GetProperty("status").GetString().ShouldBe("skipped");
        var runId=run.GetProperty("id").GetGuid();
        (await Call(HttpMethod.Get,$"/runs/{runId}")).Body.GetProperty("id").GetGuid().ShouldBe(runId);
        (await Call(HttpMethod.Get,"/runs?limit=1")).Body.GetArrayLength().ShouldBe(1);
        var (instructionsStatus,instructions)=await Call(HttpMethod.Get,"/instructions");instructionsStatus.ShouldBe(200);
        var revision=instructions.GetProperty("project").GetProperty("revision").GetInt32();
        (await Call(HttpMethod.Put,"/instructions",new {scope="project",content="Check native source evidence.",expected_revision=revision})).Status.ShouldBe(200);
        (await Call(HttpMethod.Put,"/instructions",new {scope="project",content="Stale write",expected_revision=revision})).Status.ShouldBe(409);
        var reread=(await Call(HttpMethod.Get,"/instructions")).Body.GetProperty("project");
        reread.GetProperty("content").GetString().ShouldBe("Check native source evidence.");
        reread.GetProperty("history").GetArrayLength().ShouldBe(1);
        model.Calls.ShouldBe(0);
    }

    private sealed class NoCallsModel : IDreamBatchModel
    {
        public int Calls {get;private set;}
        public bool SupportsTools(string provider)=>true;
        public Task<DreamModelResponse> CompleteAsync(DreamModelRequest request,CancellationToken ct=default)
        {Calls++;throw new InvalidOperationException("Control surfaces must not call a model.");}
    }
    private sealed class NoSourceReads : IDreamSourceReader
    {public Task<object> ExecuteAsync(Guid projectId,string tool,JsonElement arguments,CancellationToken ct=default)=>throw new InvalidOperationException("Control surfaces must not read source.");}
}
