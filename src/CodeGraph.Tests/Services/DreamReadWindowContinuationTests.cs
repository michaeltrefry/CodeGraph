using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class DreamReadWindowContinuationTests
{
    [Fact]
    public void Scripted_actions_default_to_empty_state_and_refuse_unsupported_nonempty_windows()
    {
        IKnowledgeDreamActions actions = new EmptyActions();
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), Guid.NewGuid(), "scripted");
        var state = actions.CaptureReadWindows(scope);
        state.EnumerateObject().ShouldBeEmpty();
        actions.RestoreReadWindows(scope, state);
        Should.Throw<WorkException>(() => actions.RestoreReadWindows(scope, JsonSerializer.SerializeToElement(new { version = 1 })))
            .Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        using var other = DreamExecutionScope.Begin(Guid.NewGuid(), scope.ProjectId, "scripted");
        Should.Throw<WorkException>(() => actions.CaptureReadWindows(scope)).Code.ShouldBe(WorkErrorCodes.Forbidden);
        Should.Throw<WorkException>(() => actions.RestoreReadWindows(scope, state)).Code.ShouldBe(WorkErrorCodes.Forbidden);
    }
    [Fact]
    public async Task Original_cursor_and_membership_continue_exactly_across_two_authorized_scopes_without_source_proof()
    {
        var fixture = new Fixture(3);
        JsonElement state, expected, first, summaryFirst, summaryExpected;
        var origin = Guid.NewGuid();
        using (var scope = DreamExecutionScope.Begin(origin, fixture.Project, "scripted"))
        {
            scope.MarkSourceRead("repo", "Source.cs", 7, "hash", "hash");
            summaryFirst = await Read(fixture.Service, new { limit = 1 });
            first = await Read(fixture.Service, new { detail = "full", limit = 1 });
            state = fixture.Service.CaptureReadWindows(scope);
            summaryExpected = await Read(fixture.Service, new { cursor = Cursor(summaryFirst), limit = 1 });
            expected = await Read(fixture.Service, new { cursor = Cursor(first), limit = 1 });
        }
        var secondRun = Guid.NewGuid();
        JsonElement nextState;
        using (var scope = DreamExecutionScope.Begin(secondRun, fixture.Project, "scripted"))
        {
            scope.ResumeFromRunId = origin;
            fixture.Service.RestoreReadWindows(scope, RoundTrip(state));
            (await Read(fixture.Service, new { cursor = Cursor(summaryFirst), limit = 1 })).GetRawText().ShouldBe(summaryExpected.GetRawText());
            var resumed = await Read(fixture.Service, new { cursor = Cursor(first), limit = 1 });
            resumed.GetRawText().ShouldBe(expected.GetRawText());
            scope.ReadFile("repo", "Source.cs").ShouldBeFalse();
            scope.ReadNode(7).ShouldBeFalse();
            nextState = fixture.Service.CaptureReadWindows(scope);
            nextState.GetProperty("owner_run_id").GetGuid().ShouldBe(secondRun);
            nextState.GetProperty("windows")[0].GetProperty("origin_run_id").GetGuid().ShouldBe(origin);
        }
        using (var scope = DreamExecutionScope.Begin(Guid.NewGuid(), fixture.Project, "scripted"))
        {
            scope.ResumeFromRunId = secondRun;
            fixture.Service.RestoreReadWindows(scope, nextState);
            var last = await Read(fixture.Service, new { cursor = Cursor(expected), limit = 1 });
            last.GetProperty("items")[0].GetProperty("id").GetString().ShouldBe(fixture.Proxy.References[2].Id);
            last.GetProperty("has_more").GetBoolean().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Checkpoint_preserves_exact_large_fragment_text_and_cache_without_loading_unseen_bodies()
    {
        var fixture = new Fixture(522, largeFirst: true);
        var origin = Guid.NewGuid();
        JsonElement state, first, expected;
        using (var scope = DreamExecutionScope.Begin(origin, fixture.Project, "scripted"))
        {
            first = await Read(fixture.Service, new { detail = "full", limit = 1 });
            first.GetProperty("fragment").GetProperty("has_more").GetBoolean().ShouldBeTrue();
            fixture.Proxy.Loads.ShouldBe(1);
            state = fixture.Service.CaptureReadWindows(scope);
            fixture.Proxy.Loads.ShouldBe(1);
            fixture.Proxy.Queries.ShouldBe(1);
            var descriptors = state.GetProperty("windows")[0].GetProperty("items");
            descriptors.GetArrayLength().ShouldBe(522);
            descriptors[0].GetProperty("json").GetString().ShouldBe(fixture.Proxy.Results[fixture.Proxy.References[0].Id].Value.GetRawText());
            descriptors.EnumerateArray().Skip(1).Any(item => item.TryGetProperty("json", out _)).ShouldBeFalse();
            expected = await Read(fixture.Service, new { cursor = Cursor(first), limit = 1 });
        }
        // Cached mutable content remains the captured JSON; restoring cannot turn a fragment
        // into text from a later database row or trigger another fetch of that cached row.
        fixture.Proxy.Change(0, "A different request replaced the body");
        using (var scope = DreamExecutionScope.Begin(Guid.NewGuid(), fixture.Project, "scripted"))
        {
            scope.ResumeFromRunId = origin;
            fixture.Service.RestoreReadWindows(scope, RoundTrip(state));
            fixture.Proxy.Loads.ShouldBe(1);
            var next = await Read(fixture.Service, new { cursor = Cursor(first), limit = 1 });
            next.GetRawText().ShouldBe(expected.GetRawText());
            fixture.Proxy.Loads.ShouldBe(1);
            var text = new StringBuilder(first.GetProperty("fragment").GetProperty("content").GetString());
            var page = next;
            while (true)
            {
                text.Append(page.GetProperty("fragment").GetProperty("content").GetString());
                if (!page.GetProperty("fragment").GetProperty("has_more").GetBoolean()) break;
                page = await Read(fixture.Service, new { cursor = Cursor(page), limit = 1 });
            }
            text.ToString().ShouldBe(state.GetProperty("windows")[0].GetProperty("items")[0].GetProperty("json").GetString());
        }
    }

    [Fact]
    public async Task Unloaded_mutable_trail_hash_conflicts_after_restore_instead_of_silently_restarting()
    {
        var fixture = new Fixture(2);
        var origin = Guid.NewGuid();
        JsonElement state, first;
        using (var scope = DreamExecutionScope.Begin(origin, fixture.Project, "scripted"))
        {
            first = await Read(fixture.Service, new { detail = "full", limit = 1 });
            state = fixture.Service.CaptureReadWindows(scope);
        }
        fixture.Proxy.Change(1, "Changed unread evidence");
        using var resumed = DreamExecutionScope.Begin(Guid.NewGuid(), fixture.Project, "scripted");
        resumed.ResumeFromRunId = origin;
        fixture.Service.RestoreReadWindows(resumed, state);
        (await Should.ThrowAsync<WorkException>(() => Read(fixture.Service, new { cursor = Cursor(first) }))).Code.ShouldBe(WorkErrorCodes.Conflict);
        fixture.Proxy.Queries.ShouldBe(1);
    }

    [Fact]
    public async Task Historic_entry_descriptor_keeps_version_stale_flag_and_missing_version_refusal()
    {
        var fixture = new Fixture(0);
        var origin = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var entry = Guid.NewGuid();
        fixture.Proxy.Versions[(entry, 1)] = JsonSerializer.SerializeToElement(new { id = entry, version = 1, body = "Captured behavior", possibly_stale = false });
        fixture.Proxy.Versions[(entry, 2)] = JsonSerializer.SerializeToElement(new { id = entry, version = 2, body = "New current behavior" });
        var state = JsonSerializer.SerializeToElement(new { version = 1, project_id = fixture.Project, owner_run_id = origin,
            windows = new[] { new { id = snapshot, tool = "dream_read", origin_run_id = origin, captured_at = DateTime.UtcNow,
                items = new[] { new { kind = "entry_version", cache = false, entry_id = entry, entry_version = 1, possibly_stale = true } } } } });
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), fixture.Project, "scripted");
        scope.ResumeFromRunId = origin;
        fixture.Service.RestoreReadWindows(scope, state);
        var cursor = MakeCursor(origin, fixture.Project, "dream_read", snapshot);
        var page = await Read(fixture.Service, new { cursor }, "dream_read");
        page.GetProperty("items")[0].GetProperty("version").GetInt32().ShouldBe(1);
        page.GetProperty("items")[0].GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
        fixture.Service.CaptureReadWindows(scope).GetProperty("windows")[0].GetProperty("items")[0].TryGetProperty("json", out _).ShouldBeFalse();
        fixture.Proxy.Versions.Remove((entry, 1));
        (await Should.ThrowAsync<WorkException>(() => Read(fixture.Service, new { cursor }, "dream_read"))).Code.ShouldBe(WorkErrorCodes.NotFound);
    }

    [Fact]
    public async Task Unauthorized_owner_project_version_or_descriptor_never_partially_installs_windows()
    {
        var fixture = new Fixture(2);
        var origin = Guid.NewGuid();
        JsonElement state;
        string cursor;
        using (var scope = DreamExecutionScope.Begin(origin, fixture.Project, "scripted"))
        {
            cursor = Cursor(await Read(fixture.Service, new { detail = "full", limit = 1 }));
            state = fixture.Service.CaptureReadWindows(scope);
        }
        using var resumed = DreamExecutionScope.Begin(Guid.NewGuid(), fixture.Project, "scripted");
        Should.Throw<WorkException>(() => fixture.Service.RestoreReadWindows(resumed, state)).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        resumed.ResumeFromRunId = origin;
        foreach (var mutate in new Action<JsonNode>[]
        {
            node => node["project_id"] = Guid.NewGuid(), node => node["owner_run_id"] = Guid.NewGuid(), node => node["version"] = 2,
            node => node["windows"]![0]!["origin_run_id"] = Guid.Empty,
            node => node["windows"]![0]!["tool"] = "dream_read_source",
            node => node["windows"]![0]!["items"]![1]!["kind"] = "unknown",
            node => node["windows"]![0]!["items"]![1]!["json"] = "invalid JSON",
            node => node["windows"]!.AsArray().Add(node["windows"]![0]!.DeepClone())
        })
        {
            var changed = JsonNode.Parse(state.GetRawText())!;
            mutate(changed);
            Should.Throw<WorkException>(() => fixture.Service.RestoreReadWindows(resumed, JsonSerializer.SerializeToElement(changed))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
            (await Should.ThrowAsync<WorkException>(() => Read(fixture.Service, new { cursor }))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        }
        fixture.Service.RestoreReadWindows(resumed, state);
        Should.Throw<WorkException>(() => fixture.Service.RestoreReadWindows(resumed, state)).Code.ShouldBe(WorkErrorCodes.Conflict);
        await Read(fixture.Service, new { cursor });
    }

    [Fact]
    public async Task Only_loaded_snapshot_origin_project_tool_and_offsets_authorize_an_old_cursor()
    {
        var fixture = new Fixture(2);
        var origin = Guid.NewGuid();
        JsonElement state;
        string cursor;
        using (var scope = DreamExecutionScope.Begin(origin, fixture.Project, "scripted"))
        {
            cursor = Cursor(await Read(fixture.Service, new { detail = "full", limit = 1 }));
            state = fixture.Service.CaptureReadWindows(scope);
        }
        using var resumed = DreamExecutionScope.Begin(Guid.NewGuid(), fixture.Project, "scripted");
        resumed.ResumeFromRunId = origin;
        (await Should.ThrowAsync<WorkException>(() => Read(fixture.Service, new { cursor }))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        fixture.Service.RestoreReadWindows(resumed, state);
        foreach (var mutation in new Action<JsonNode>[] { node => node["Run"] = Guid.NewGuid(), node => node["Project"] = Guid.NewGuid(),
            node => node["Snapshot"] = Guid.NewGuid(), node => node["Item"] = 99, node => node["Character"] = -1 })
        {
            var forged = JsonNode.Parse(Convert.FromBase64String(cursor[3..]))!;
            mutation(forged);
            var bad = "dr:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(forged));
            (await Should.ThrowAsync<WorkException>(() => Read(fixture.Service, new { cursor = bad }))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        }
        (await Should.ThrowAsync<WorkException>(() => Read(fixture.Service, new { cursor }, "dream_read"))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        await Read(fixture.Service, new { cursor });
        using var foreign = DreamExecutionScope.Begin(Guid.NewGuid(), Guid.NewGuid(), "scripted");
        Should.Throw<WorkException>(() => fixture.Service.CaptureReadWindows(resumed)).Code.ShouldBe(WorkErrorCodes.Forbidden);
        Should.Throw<WorkException>(() => fixture.Service.RestoreReadWindows(resumed, state)).Code.ShouldBe(WorkErrorCodes.Forbidden);
        foreign.ResumeFromRunId = origin;
        Should.Throw<WorkException>(() => fixture.Service.RestoreReadWindows(foreign, state)).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    private static string Cursor(JsonElement page) => page.GetProperty("next_cursor").GetString()!;
    private static JsonElement RoundTrip(JsonElement value) => JsonSerializer.Deserialize<JsonElement>(value.GetRawText());
    private static string MakeCursor(Guid run, Guid project, string tool, Guid snapshot) => "dr:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { Run = run, Project = project, Tool = tool, Snapshot = snapshot, Item = 0, Character = 0 }));
    private static async Task<JsonElement> Read(IKnowledgeDreamActions actions, object input, string tool = "dream_work_trail")
    {
        var value = await actions.ExecuteToolAsync(tool, JsonSerializer.SerializeToElement(input), DreamExecutionScope.Current!.Actor);
        var json = JsonSerializer.SerializeToElement(value, value.GetType(), WorkJson.Options);
        Encoding.UTF8.GetByteCount(json.GetRawText()).ShouldBeLessThan(12_100);
        return json.GetProperty("data");
    }
    private sealed class Fixture
    {
        public Guid Project { get; } = Guid.NewGuid();
        public ReadWindowStoreProxy Proxy { get; }
        public WorkService Service { get; }
        public Fixture(int count, bool largeFirst = false)
        {
            var store = DispatchProxy.Create<IReadWindowStore, ReadWindowStoreProxy>();
            Proxy = (ReadWindowStoreProxy)(object)store;
            Proxy.Project = Project;
            for (var index = 0; index < count; index++)
            {
                var id = Guid.NewGuid().ToString();
                var body = index == 0 && largeFirst ? string.Concat(Enumerable.Repeat("界😀café", 6000)) : $"BODY_{index:D4}_" + new string('x', 100);
                var value = JsonSerializer.Deserialize<JsonElement>("{ \"kind\": \"message\", \"id\": \"" + id + "\", \"data\": { \"body\": \"" + body + "\" } }");
                var hash = Hash(value);
                Proxy.References.Add(new("message", id, null, DateTime.UnixEpoch.AddSeconds(index), hash));
                Proxy.Results[id] = (hash, value);
            }
            Service = new(store);
        }
    }
    public interface IReadWindowStore : IWorkStore, IDreamingKnowledgeStore { }
    public class ReadWindowStoreProxy : DispatchProxy
    {
        public Guid Project;
        public int Loads, Queries;
        public readonly List<DreamTrailReference> References = [];
        public readonly Dictionary<string, (string Hash, JsonElement Value)> Results = [];
        public readonly Dictionary<(Guid Id, long Version), JsonElement> Versions = [];
        public void Change(int index, string body)
        {
            var value = JsonSerializer.SerializeToElement(new { kind = "message", id = References[index].Id, data = new { body } });
            Results[References[index].Id] = (Hash(value), value);
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ((Guid)args![0]!).ShouldBe(Project);
            if (method!.Name == nameof(IDreamingKnowledgeStore.QueryDreamWorkTrailAsync))
            { Queries++; return Task.FromResult<IReadOnlyList<DreamTrailReference>>(References.ToArray()); }
            if (method.Name == nameof(IDreamingKnowledgeStore.ReadDreamWorkTrailItemAsync))
            {
                Loads++;
                var reference = (DreamTrailReference)args[1]!;
                var current = Results[reference.Id];
                return current.Hash == reference.ContentHash ? Task.FromResult(current.Value)
                    : Task.FromException<JsonElement>(new WorkException(WorkErrorCodes.Conflict, "Captured hash changed"));
            }
            if (method.Name == nameof(IDreamingKnowledgeStore.ReadDreamKnowledgeVersionAsync))
            {
                Loads++;
                return Versions.TryGetValue(((Guid)args[1]!, (long)args[2]!), out var value) ? Task.FromResult(value)
                    : Task.FromException<JsonElement>(WorkException.NotFound("Captured version is unavailable"));
            }
            throw new InvalidOperationException($"Unexpected store call {method.Name}");
        }
    }
    private static string Hash(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
    private sealed class EmptyActions : IKnowledgeDreamActions
    {
        public Task<DreamDigest> BuildDigestAsync(Guid projectId, DateTime? since, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<object> ExecuteToolAsync(string tool, JsonElement args, WorkActor actor, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DreamRevertResult> RevertAsync(Guid projectId, Guid runId, WorkActor actor, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
