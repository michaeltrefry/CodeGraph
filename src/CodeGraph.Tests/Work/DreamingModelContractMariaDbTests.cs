using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Fact]
    public async Task Published_private_schema_and_reader_responses_are_sufficient_to_verify_and_propose_without_identity_arguments()
    {
        var (project, original) = await StaleFactAsync();
        JsonElement discovered = default;
        JsonElement verified = default;
        var turn = 0;
        // Fixture identifiers are deliberately not captured by this model. Its only inputs are
        // the published schema and real tool responses, just like a production model client.
        var model = new ScriptedModel((request, cancellationToken) =>
        {
            DreamModelResponse CallPublished(string id, string name, object arguments)
            {
                var tool = request.Tools.Single(tool => tool.Name == name);
                var args = JsonSerializer.SerializeToElement(arguments, WorkJson.Options);
                var properties = tool.Parameters.GetProperty("properties");
                foreach (var property in args.EnumerateObject()) properties.TryGetProperty(property.Name, out _).ShouldBeTrue(property.Name);
                if (tool.Parameters.TryGetProperty("required", out var required))
                    foreach (var field in required.EnumerateArray()) args.TryGetProperty(field.GetString()!, out _).ShouldBeTrue(field.GetString());
                foreach (var identity in new[] { "project", "session_id", "run_id", "actor" }) args.TryGetProperty(identity, out _).ShouldBeFalse();
                return Response(new DreamToolCall(id, name, args));
            }
            JsonElement Reply(string id) => JsonDocument.Parse(request.Messages.Single(message => message.ToolCallId == id).Content!).RootElement.Clone();
            var response = ++turn switch
            {
                1 => CallPublished("find", "dream_read", new { possibly_stale = true, detail = "full" }),
                2 => ReadSource(),
                3 => VerifyDiscovered(),
                4 => ProposeDiscovered(),
                _ => Response()
            };
            return Task.FromResult(response);
            DreamModelResponse ReadSource()
            {
                discovered = Reply("find").GetProperty("data").GetProperty("items").EnumerateArray().Single();
                var file = discovered.GetProperty("verified_files").EnumerateArray().Single();
                return CallPublished("source", "dream_read_source", new { repository = file.GetProperty("repository").GetString(), path = file.GetProperty("path").GetString() });
            }
            DreamModelResponse VerifyDiscovered()
            {
                Reply("source").TryGetProperty("untrusted_data", out _).ShouldBeTrue();
                var evidenceSchema = request.Tools.Single(tool => tool.Name == "dream_verify").Parameters.GetProperty("properties").GetProperty("evidence").GetProperty("items");
                evidenceSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldBe(new[] { "kind", "reference", "summary" });
                return CallPublished("verify", "dream_verify", new { id = discovered.GetProperty("id").GetString(), expected_version = discovered.GetProperty("version").GetInt64(),
                    evidence = new[] { new { kind = "entry", reference = discovered.GetProperty("id").GetString(), summary = "Read the referenced source this run; current behavior supports the entry." } }, prevalence = "1 entry checked against 1 source file this run" });
            }
            DreamModelResponse ProposeDiscovered()
            {
                verified = Reply("verify").GetProperty("data").GetProperty("entry");
                var citationSchema = request.Tools.Single(tool => tool.Name == "dream_propose").Parameters.GetProperty("properties").GetProperty("evidence").GetProperty("items");
                citationSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldBe(new[] { "kind", "id" });
                return CallPublished("propose", "dream_propose", new { action = "supersede", target = verified.GetProperty("id").GetString(), expected_version = verified.GetProperty("version").GetInt64(),
                    rationale = "High confidence: proposed wording explicitly identifies the checked method without changing behavior.", prevalence = "1 verified entry and 1 checked file",
                    evidence = new[] { new { kind = "entry", id = verified.GetProperty("id").GetString() } },
                    replacement = new { kind = "fact", summary = verified.GetProperty("summary").GetString() + " (checked against current source)", confidence = "high" } });
            }
        });
        var run = await (await ServiceAsync(model)).RunAsync(project, "run_now", Human);
        run.Status.ShouldBe("completed", run.Reason);
        SuccessfulWrites(run, 4);
        discovered.GetProperty("id").GetGuid().ShouldBe(original.Id);
        var after = (await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(original.Id, false))!;
        after.PossiblyStale.ShouldBeFalse();
        after.Status.ShouldBe(KnowledgeStatus.Active);
        var proposal = ProposalResult(run.Actions.Single(action => action.Tool == "dream_propose")).Proposal!;
        proposal.RunId.ShouldBe(run.Id);
        proposal.ProjectId.ShouldBe(project);
        proposal.Target.ShouldBe(original.Id);
        run.Actions.Single(action => action.Tool == "dream_verify").Evidence.Single().Summary.ShouldContain("current behavior");
    }

    [Fact]
    public async Task Private_identity_overrides_and_foreign_targets_are_forbidden_and_leave_every_entry_unchanged()
    {
        var project = await ProjectAsync();
        var foreignProject = await ProjectAsync("inference");
        var own = await RecordAsync(project, "Local contract target");
        var foreign = await RecordAsync(foreignProject, "Foreign contract target");
        var before = JsonSerializer.Serialize(await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(own.Id, true), WorkJson.Options);
        var foreignBefore = JsonSerializer.Serialize(await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(foreign.Id, true), WorkJson.Options);
        JsonObject Proposal(Guid target) => JsonSerializer.SerializeToNode(new { action = "retract", target, expected_version = 1,
            rationale = "A tampered target must never be proposed", prevalence = "one entry", evidence = new[] { new { kind = "entry", id = own.Id.ToString() } } }, WorkJson.Options)!.AsObject();
        var calls = new List<DreamModelResponse>();
        foreach (var field in new[] { "project", "project_id", "session_id", "run_id", "actor" })
        {
            var args = Proposal(own.Id); args[field] = Guid.NewGuid().ToString();
            calls.Add(Response(Call(field, "dream_propose", args)));
        }
        calls.Add(Response(Call("foreign", "dream_propose", Proposal(foreign.Id))));
        calls.Add(Response(Call("foreign_read", "dream_read", new { id = foreign.Id })));
        calls.Add(Response());
        var run = await (await ServiceAsync(new ScriptedModel(calls.ToArray()))).RunAsync(project, "run_now", Human);
        run.Status.ShouldBe("completed", run.Reason);
        run.Actions.Count.ShouldBe(7);
        run.Actions.ShouldAllBe(action => action.Result.GetProperty("error").GetString() == WorkErrorCodes.Forbidden);
        JsonSerializer.Serialize(await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(own.Id, true), WorkJson.Options).ShouldBe(before);
        JsonSerializer.Serialize(await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(foreign.Id, true), WorkJson.Options).ShouldBe(foreignBefore);
        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dream_proposals WHERE project_id IN @ids", new { ids = new[] { project, foreignProject } })).ShouldBe(0);
    }

    [Fact]
    public async Task Malformed_evidence_and_source_arguments_return_structured_errors_without_aborting_the_loop()
    {
        var (project, entry) = await StaleFactAsync();
        var model = new ScriptedModel(
            Response(Call("no_summary", "dream_verify", new { id = entry.Id, expected_version = entry.Version,
                evidence = new[] { new { kind = "entry", reference = entry.Id.ToString() } }, prevalence = "one entry" })),
            Response(Call("wrong_field", "dream_verify", new { id = entry.Id, expected_version = entry.Version,
                evidence = new[] { new { kind = "entry", id = entry.Id.ToString(), summary = "wrong direct-write shape" } }, prevalence = "one entry" })),
            Response(Call("unknown", "dream_search_graph", new { repository = "codegraph", name_patern = "Source" })),
            Response(Call("label", "dream_search_graph", new { repository = "codegraph", label = "MisspeltLabel" })),
            Response(Call("limit", "dream_search_graph", new { repository = "codegraph", limit = 51 })),
            Response(Call("node", "dream_read_source", new { node_id = "Source.cs" })),
            Response(Call("override", "dream_read_source", new { repository = "codegraph", path = "Source.cs", run_id = Guid.NewGuid() })),
            Response());
        var run = await (await ServiceAsync(model)).RunAsync(project, "run_now", Human);
        run.Status.ShouldBe("completed", run.Reason);
        run.Actions.Count.ShouldBe(7);
        foreach (var action in run.Actions.Take(6)) action.Result.GetProperty("error").GetString().ShouldBe(WorkErrorCodes.InvalidArgument);
        run.Actions.Last().Result.GetProperty("error").GetString().ShouldBe(WorkErrorCodes.Forbidden);
        var after = (await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(entry.Id, true))!;
        after.Version.ShouldBe(entry.Version);
        after.PossiblyStale.ShouldBeTrue();
        after.Revisions!.Count.ShouldBe(1);
    }
}
