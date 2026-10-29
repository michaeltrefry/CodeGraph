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

        tools.Select(tool => tool.ProtocolTool.Name).ShouldBe(["create_work", "get_work", "find_work", "update_work", "next_work", "changes"]);
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
    public async Task Without_the_mariadb_store_the_tools_answer_unavailable()
    {
        var exception = await Should.ThrowAsync<WorkException>(() =>
            new WorkService().ExecuteAsync(WorkTools.GetWork, Json("""{"id":"SW-1"}"""), new WorkActor("alice")));
        exception.Code.ShouldBe(WorkErrorCodes.Unavailable);
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
        public Task<ItemView?> GetItemAsync(Guid itemId, CancellationToken ct = default) => throw Fail();
        public Task<FindWorkResult> FindAsync(FindWorkSpec spec, CancellationToken ct = default) => throw Fail();
        public Task<long> GetChangeHeadAsync(CancellationToken ct = default) => throw Fail();
        public Task<IReadOnlyList<ChangeView>> ReadChangesAsync(Guid? projectId, long afterSequence, int limit, CancellationToken ct = default) => throw Fail();
    }
}
