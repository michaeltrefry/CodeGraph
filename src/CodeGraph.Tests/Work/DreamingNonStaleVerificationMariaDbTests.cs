using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Fact]
    public async Task Capture_freshness_metadata_does_not_replace_an_explicit_source_verification_revision()
    {
        var project = await ProjectAsync();
        var directory = Path.Combine(root, "codegraph");
        Directory.CreateDirectory(directory);
        const string source = "public static class Source { public static int Answer() => 42; }";
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await File.WriteAllTextAsync(Path.Combine(directory, "Source.cs"), source);
        await graph.UpsertRepositoryAsync(new RepositoryEntity { Name = "codegraph", LocalPath = directory, LastCommitSha = commit });
        await graph.UpsertFileHashBatchAsync("codegraph", new Dictionary<string, string> { ["Source.cs"] = Hash(source) });
        var entry = (await ((IKnowledgeStore)db.Store).RecordKnowledgeAsync(project,
            new(KnowledgeKind.Fact, "Source.Answer returns 42", "A source-scoped claim captured before explicit verification.", KnowledgeConfidence.High,
                null, null, ["codegraph"], ["Source.cs"], [], commit), Human, null)).Entry;
        JsonElement full = default;
        var turn = 0;
        var model = new ScriptedModel((request, _) => Task.FromResult(++turn switch
        {
            1 => Response(Call("full", "dream_read", new { id = entry.Id })),
            2 => ReadOwningSource(request),
            3 => Response(Call("verify", "dream_verify", new { id = full.GetProperty("id").GetString(), expected_version = full.GetProperty("version").GetInt64(),
                evidence = new[] { new { kind = "entry", reference = full.GetProperty("id").GetString(), summary = "Read codegraph/Source.cs this run; the whole claim that Source.Answer returns 42 is supported." } },
                prevalence = "1 non-stale captured fact checked against 1 owning source file" })),
            _ => Response()
        }));
        DreamModelResponse ReadOwningSource(DreamModelRequest request)
        {
            using var response = JsonDocument.Parse(request.Messages.Single(message => message.ToolCallId == "full").Content!);
            full = response.RootElement.GetProperty("data").GetProperty("items")[0].Clone();
            full.GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();
            full.GetProperty("verified_at").GetDateTime().ShouldNotBe(default);
            full.GetProperty("verified_commit").GetString().ShouldBe(commit);
            full.GetProperty("revisions").EnumerateArray().Select(revision => revision.GetProperty("action").GetString()).ShouldBe(new[] { "created" });
            var file = full.GetProperty("verified_files")[0];
            return Response(Call("source", "dream_read_source", new { repository = file.GetProperty("repository").GetString(), path = file.GetProperty("path").GetString() }));
        }

        var service = await ServiceAsync(model);
        var run = await service.RunAsync(project, "run_now", Human);

        run.Status.ShouldBe("completed", run.Reason);
        SuccessfulWrites(run, 3);
        model.Requests[0].System.ShouldContain("Capture can initialize verification timestamps");
        var after = (await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(entry.Id, true))!;
        after.PossiblyStale.ShouldBeFalse();
        after.Summary.ShouldBe(entry.Summary);
        after.Revisions!.First().Action.ShouldBe("verified");
        after.Revisions.First().Author.SessionId.ShouldBe(run.Id.ToString());
        after.Version.ShouldBe(entry.Version + 1);
    }
}
