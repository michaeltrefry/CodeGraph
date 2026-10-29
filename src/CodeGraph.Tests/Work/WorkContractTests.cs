using System.Reflection;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The strict contract of the work tools (E1), without a database: unknown, misspelt and mistyped
/// arguments are refused invalid_argument naming the valid ones, at every level, and the MCP
/// schemas are generated from the same types (additionalProperties false everywhere).
/// </summary>
public sealed class WorkContractTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static WorkException Refusal<T>(string json, string tool = "tool") where T : class =>
        Should.Throw<WorkException>(() => WorkJson.Parse<T>(Json(json), tool));

    [Fact]
    public void An_unknown_top_level_argument_is_refused_listing_the_valid_ones()
    {
        var refusal = Refusal<UpdateWorkRequest>("""{"id":"SW-1","expected_versoin":3,"title":"x"}""", "update_work");

        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refusal.Message.ShouldContain("'expected_versoin'");
        refusal.Message.ShouldContain("expected_version");
        refusal.Message.ShouldContain("add_blocked_by");
    }

    [Fact]
    public void A_misspelt_field_of_a_plan_item_is_refused_naming_the_fields_of_a_plan_item()
    {
        var refusal = Refusal<CreateWorkRequest>(
            """{"project":"SW","idempotency_key":"k","plan":{"items":[{"ref":"a","title":"A","blockd_by":["b"]}]}}""", "create_work");

        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refusal.Message.ShouldContain("blockd_by");
        refusal.Message.ShouldContain("blocked_by");
        refusal.Message.ShouldContain("ref");
        // The fields of a plan item, not of the top level.
        refusal.Message.ShouldNotContain("new_project");
    }

    [Theory]
    [InlineData("""{"id":"SW-1","expected_version":"3","title":"x"}""", "expected_version")]
    [InlineData("""{"id":"SW-1","expected_version":3,"status":2}""", "status")]
    [InlineData("""{"id":"SW-1","expected_version":3,"status":"done"}""", "status")]
    [InlineData("""{"id":"SW-1","expected_version":3,"labels":"a,b"}""", "labels")]
    [InlineData("""{"id":"SW-1","expected_version":3,"claim":{"marker":"m","ttl_seconds":"60"}}""", "claim.ttl_seconds")]
    public void A_mistyped_argument_is_refused_naming_it(string json, string argument)
    {
        var refusal = Refusal<UpdateWorkRequest>(json, "update_work");

        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refusal.Message.ShouldContain($"'{argument}'");
    }

    [Fact]
    public void A_missing_required_argument_is_refused_naming_the_required_ones()
    {
        var refusal = Refusal<UpdateWorkRequest>("""{"id":"SW-1","title":"x"}""", "update_work");

        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refusal.Message.ShouldContain("expected_version");
    }

    [Fact]
    public void Statuses_are_read_and_written_as_snake_case()
    {
        var request = WorkJson.Parse<UpdateWorkRequest>(Json("""{"id":"SW-1","expected_version":1,"status":"in_progress"}"""), "update_work");

        request.Status.ShouldBe(WorkStatus.InProgress);
        JsonSerializer.Serialize(WorkStatus.InReview, WorkJson.Options).ShouldBe("\"in_review\"");
        WorkJson.Names<WorkStatus>().ShouldBe("backlog, ready, in_progress, in_review, complete, closed");
    }

    [Fact]
    public void Every_tool_schema_is_closed_at_every_level_and_matches_its_argument_type()
    {
        foreach (var tool in WorkTools.All)
        {
            var schema = WorkJson.SchemaOf(tool.ArgumentsType);
            schema.GetProperty("type").ToString().ShouldContain("object");
            AssertClosed(schema, tool.Name);

            var declared = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToList();
            var members = WorkJson.MemberList(tool.ArgumentsType).Split(", ").Order().ToList();
            declared.ShouldBe(members, tool.Name);
        }
    }

    [Fact]
    public void The_update_schema_lists_the_statuses_and_marks_id_and_expected_version_required()
    {
        var schema = WorkJson.SchemaOf(typeof(UpdateWorkRequest));

        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldBe(["id", "expected_version"], ignoreOrder: true);
        var status = schema.GetProperty("properties").GetProperty("status");
        status.GetProperty("enum").EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Null ? null : value.GetString())
            .Where(value => value is not null)
            .ShouldBe(["backlog", "ready", "in_progress", "in_review", "complete", "closed"]);
        status.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Mcp_tools_carry_the_generated_schema_and_the_read_only_hints()
    {
        var tools = WorkMcpTool.CreateAll().ToList();

        tools.Select(tool => tool.ProtocolTool.Name).ShouldBe(["create_work", "get_work", "find_work", "update_work", "next_work", "changes", "start_session", "end_session", "get_briefing", "acquire", "renew_hold", "release", "sync_plan"]);
        foreach (var tool in tools)
        {
            var definition = WorkTools.Find(tool.ProtocolTool.Name)!;
            tool.ProtocolTool.Annotations!.ReadOnlyHint.ShouldBe(definition.ReadOnly);
            tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("SW-142", "SW", WorkEntityKind.Item, 142)]
    [InlineData("sw-f12", "SW", WorkEntityKind.Feature, 12)]
    [InlineData("CG2-7", "CG2", WorkEntityKind.Item, 7)]
    public void Keys_parse_into_project_kind_and_number(string key, string project, WorkEntityKind kind, int number)
    {
        WorkKeys.TryParse(key, out var parsedProject, out var parsedKind, out var parsedNumber).ShouldBeTrue();
        (parsedProject, parsedKind, parsedNumber).ShouldBe((project, kind, number));
    }

    [Theory]
    [InlineData("SW")]
    [InlineData("SW-0")]
    [InlineData("SW-F")]
    [InlineData("S-1")]
    [InlineData("SW-1x")]
    public void Malformed_keys_do_not_parse(string key) =>
        WorkKeys.TryParse(key, out _, out _, out _).ShouldBeFalse();

    [Fact]
    public async Task Calls_are_refused_before_the_store_is_reached()
    {
        // A store that fails every call: a refusal must come from the arguments alone.
        var service = new WorkService(new UnreachableStore());

        var exception = await Should.ThrowAsync<WorkException>(() =>
            service.ExecuteAsync(WorkTools.NextWork, Json("""{"project":"SW","marker":"m","anther":true}"""), new WorkActor("alice")));
        exception.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        exception.Message.ShouldContain("another");

        var two = await Should.ThrowAsync<WorkException>(() =>
            service.ExecuteAsync(WorkTools.CreateWork, Json("""{"project":"SW","item":{"title":"a"},"feature":{"title":"b"}}"""), new WorkActor("alice")));
        two.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        two.Message.ShouldContain("exactly one of new_project, feature, item, plan");
    }

    [Fact]
    public async Task Without_the_mariadb_store_the_tools_fail_plainly_with_no_error_code()
    {
        // Not a work error: it is outside the closed vocabulary, so it is not a WorkException.
        await Should.ThrowAsync<WorkStoreUnavailableException>(() =>
            new WorkService().ExecuteAsync(WorkTools.GetWork, Json("""{"id":"SW-1"}"""), new WorkActor("alice")));
    }

    [Fact]
    public void The_error_vocabulary_is_exactly_the_seven_codes_of_e1()
    {
        typeof(WorkErrorCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ShouldBe(["not_found", "conflict", "already_claimed", "cycle", "invalid_argument", "busy", "claim_lost"], ignoreOrder: true);
    }

    // Every enum field of every request, with a template whose {0} is the JSON of the value and a
    // valid wire name for it.
    public static TheoryData<Type, string, string, string> EnumFields => new()
    {
        { typeof(UpdateWorkRequest), """{"id":"X-1","expected_version":1,"status":{0}}""", "ready", "status" },
        { typeof(UpdateWorkRequest), """{"id":"X-1","expected_version":1,"type":{0}}""", "bug", "type" },
        { typeof(UpdateWorkRequest), """{"id":"X-1","expected_version":1,"priority":{0}}""", "high", "priority" },
        { typeof(UpdateWorkRequest), """{"id":"X-1","expected_version":1,"add_links":[{"kind":{0},"url":"https://x.example/1"}]}""", "pr", "kind" },
        { typeof(FindWorkRequest), """{"project":"SW","kind":{0}}""", "feature", "kind" },
        { typeof(FindWorkRequest), """{"project":"SW","status":[{0}]}""", "in_review", "status" },
        { typeof(FindWorkRequest), """{"project":"SW","type":{0}}""", "task", "type" },
        { typeof(FindWorkRequest), """{"project":"SW","priority":{0}}""", "critical", "priority" },
        { typeof(FindWorkRequest), """{"project":"SW","detail":{0}}""", "full", "detail" },
        { typeof(NextWorkRequest), """{"project":"SW","marker":"m","type":{0}}""", "story", "type" },
        { typeof(CreateWorkRequest), """{"project":"SW","item":{"title":"t","status":{0}}}""", "in_progress", "status" },
        { typeof(CreateWorkRequest), """{"project":"SW","item":{"title":"t","type":{0}}}""", "bug", "type" },
        { typeof(CreateWorkRequest), """{"project":"SW","item":{"title":"t","priority":{0}}}""", "low", "priority" },
        { typeof(CreateWorkRequest), """{"project":"SW","idempotency_key":"k","plan":{"items":[{"ref":"a","title":"t","status":{0}}]}}""", "closed", "status" },
        { typeof(CreateWorkRequest), """{"project":"SW","idempotency_key":"k","plan":{"items":[{"ref":"a","title":"t","type":{0}}]}}""", "task", "type" },
        { typeof(CreateWorkRequest), """{"project":"SW","idempotency_key":"k","plan":{"items":[{"ref":"a","title":"t","priority":{0}}]}}""", "medium", "priority" },
        { typeof(CreateWorkRequest), """{"new_project":{"key":"SW","name":"n","repositories":[{"repository":"x","role":{0}}]}}""", "dependency", "role" },
    };

    [Theory]
    [MemberData(nameof(EnumFields))]
    public void An_enum_is_read_only_as_its_exact_snake_case_name(Type request, string template, string valid, string field)
    {
        MethodInfo parse = typeof(WorkJson).GetMethods().Single(method => method.Name == nameof(WorkJson.Parse) && method.GetParameters()[0].ParameterType == typeof(JsonElement)).MakeGenericMethod(request);
        object? Read(string json) => parse.Invoke(null, [Json(json), "tool"]);

        Should.NotThrow(() => Read(template.Replace("{0}", $"\"{valid}\"")));

        // Comma-combined, any other casing, numbers (as number or text), the empty string, an
        // unknown name, a bool and null are all refused, naming the valid values.
        foreach (var bad in new[]
                 {
                     "\"ready, in_progress\"", "\"story, bug\"", $"\"{valid},{valid}\"", "\"InProgress\"", "\"IN_PROGRESS\"", $"\"{char.ToUpperInvariant(valid[0])}{valid[1..]}\"",
                     "2", "\"2\"", "\"\"", "\"nonsense\"", "true", "{}"
                 })
        {
            var exception = Should.Throw<TargetInvocationException>(() => Read(template.Replace("{0}", bad)), bad);
            var refusal = exception.InnerException.ShouldBeOfType<WorkException>(bad);
            refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument, bad);
            refusal.Message.ShouldContain(field, Case.Insensitive, bad);
            refusal.Message.ShouldContain(" one of ", Case.Insensitive, bad);
            refusal.Message.ShouldContain(valid, Case.Insensitive, bad);
        }
    }

    [Fact]
    public void Enums_are_written_as_their_snake_case_names()
    {
        System.Text.Json.JsonSerializer.Serialize(WorkStatus.InReview, WorkJson.Options).ShouldBe("\"in_review\"");
        System.Text.Json.JsonSerializer.Serialize(new { status = WorkStatus.InProgress, type = (WorkItemType?)null }, WorkJson.Options).ShouldBe("""{"status":"in_progress"}""");
    }

    [Theory]
    [InlineData("""{"id":"X-1","Expected_Version":1,"title":"x"}""", "Expected_Version")]
    [InlineData("""{"id":"X-1","ExpectedVersion":1,"title":"x"}""", "ExpectedVersion")]
    [InlineData("""{"ID":"X-1","expected_version":1}""", "ID")]
    [InlineData("""{"id":"X-1","expected_version":1,"add_links":[{"Kind":"pr","url":"https://x.example/1"}]}""", "Kind")]
    public void Argument_names_are_compared_exactly(string json, string named)
    {
        var refusal = Refusal<UpdateWorkRequest>(json, "update_work");

        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refusal.Message.ShouldContain(named);
        refusal.Message.ShouldContain("Valid arguments");
    }

    [Theory]
    [InlineData("""{"id":"X-1","id":"X-2","expected_version":1}""")]
    [InlineData("""{"id":"X-1","expected_version":1,"expected_version":9}""")]
    [InlineData("""{"id":"X-1","expected_version":1,"add_links":[{"kind":"pr","kind":"branch","url":"https://x.example/1"}]}""")]
    public void An_argument_passed_twice_is_refused_not_resolved_by_taking_the_last(string json)
    {
        var refusal = Refusal<UpdateWorkRequest>(json, "update_work");

        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refusal.Message.ShouldContain("twice");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("{")]
    [InlineData("""{"id": }""")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("""{"id":"X-1","id":"X-2"}""")]
    [InlineData("""{"a":{"b":1,"b":2}}""")]
    public void A_body_that_is_empty_malformed_not_an_object_or_repeats_a_member_is_invalid_argument(string body)
    {
        var refusal = Should.Throw<WorkException>(() => WorkJson.ReadObject(System.Text.Encoding.UTF8.GetBytes(body), "update_work"));

        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refusal.Message.ShouldContain("update_work");
    }

    [Fact]
    public void A_well_formed_object_body_is_read()
    {
        WorkJson.ReadObject("""{"id":"X-1","expected_version":1}"""u8.ToArray(), "update_work").GetProperty("id").GetString().ShouldBe("X-1");
    }

    private static void AssertClosed(JsonElement schema, string where)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (schema.TryGetProperty("properties", out var properties))
        {
            schema.TryGetProperty("additionalProperties", out var additional).ShouldBeTrue($"{where} has no additionalProperties");
            additional.GetBoolean().ShouldBeFalse(where);
            foreach (var property in properties.EnumerateObject())
                AssertClosed(property.Value, $"{where}.{property.Name}");
        }

        if (schema.TryGetProperty("items", out var items))
            AssertClosed(items, $"{where}[]");
    }

    private sealed class UnreachableStore : IWorkStore
    {
        private static Exception Fail() => new InvalidOperationException("The store must not be reached.");
        public Task<WorkCreated<ProjectView>> CreateProjectAsync(NewProjectSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) => throw Fail();
        public Task<ProjectView> UpdateProjectAsync(Guid projectId, long expectedVersion, ProjectPatchSpec patch, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<WorkCreated<FeatureView>> CreateFeatureAsync(Guid projectId, NewFeatureSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) => throw Fail();
        public Task<FeatureView> UpdateFeatureAsync(Guid featureId, long expectedVersion, FeaturePatchSpec patch, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<WorkCreated<ItemView>> CreateItemAsync(Guid projectId, NewItemSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) => throw Fail();
        public Task<ItemView> UpdateItemAsync(Guid itemId, long expectedVersion, ItemPatchSpec patch, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<PlanResult> ImportPlanAsync(Guid projectId, PlanSpec plan, WorkActor actor, string keyHash, CancellationToken ct = default) => throw Fail();
        public Task<NextWorkResult> NextWorkAsync(NextWorkSpec spec, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<WorkTarget?> ResolveAsync(string idOrKey, CancellationToken ct = default) => throw Fail();
        public Task<ProjectView?> GetProjectAsync(Guid projectId, CancellationToken ct = default) => throw Fail();
        public Task<FeatureView?> GetFeatureAsync(Guid featureId, CancellationToken ct = default) => throw Fail();
        public Task<ItemView?> GetItemAsync(Guid itemId, bool fullHandoffs, CancellationToken ct = default) => throw Fail();
        public Task<FindWorkResult> FindAsync(FindWorkSpec spec, CancellationToken ct = default) => throw Fail();
        public Task<long> GetChangeHeadAsync(CancellationToken ct = default) => throw Fail();
        public Task<IReadOnlyList<ChangeView>> ReadChangesAsync(Guid? projectId, long afterSequence, int limit, CancellationToken ct = default) => throw Fail();
        public Task<WorkCreated<SessionView>> StartSessionAsync(NewSessionSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) => throw Fail();
        public Task<WorkActor> TouchSessionAsync(Guid sessionId, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<EndSessionResult> EndSessionAsync(Guid sessionId, Guid? itemId, HandoffSpec? handoff, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken ct = default) => throw Fail();
        public Task<BriefingView> ReadBriefingAsync(Guid sessionId, Guid? focusItemId, CancellationToken ct = default) => throw Fail();
        public Task<Guid?> FindOwningProjectAsync(string repository, CancellationToken ct = default) => throw Fail();
        public Task<AcquireResult> AcquireAsync(AcquireSpec spec, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<HoldView> RenewHoldAsync(Guid holdId, int? ttlSeconds, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<ReleaseResult> ReleaseHoldAsync(Guid holdId, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<IReadOnlyList<WorkArea>> ReadLiveWorkAreasAsync(Guid projectId, CancellationToken ct = default) => throw Fail();
        public Task<SyncPlanPreview> PreviewPlanAsync(Guid featureId, RevisedPlanSpec plan, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<SyncPlanApplied> ApplyPlanAsync(Guid previewId, Guid? projectId, WorkActor actor, CancellationToken ct = default) => throw Fail();
        public Task<IReadOnlyList<NoticeView>> TakeNoticesAsync(WorkActor actor, CancellationToken ct = default) => throw Fail();
    }
}
