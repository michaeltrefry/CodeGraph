using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class DreamContinuationContextTests
{
    [Fact]
    public void Frozen_audit_preserves_raw_unicode_and_character_offsets_across_persistence_and_rehydration()
    {
        using var raw=JsonDocument.Parse("{ \"source\" : \"界😀 café <tag>\" }");
        var action=new DreamAction("dream_read_source",JsonSerializer.SerializeToElement(new {path="x.cs"}),raw.RootElement.Clone(),[],DateTime.UtcNow);
        var state=new DreamContinuationState {SweepStartedAt=DateTime.UtcNow,DeliveredPrefix=3,
            Messages=[new("user","digest"),new("assistant",null,[new("id",action.Tool,action.Arguments)]),new("tool","{\"replay_archive\":true}",ToolCallId:"id")],
            Replay=[DreamFrozenAction.Freeze(action)]};
        var restored=DreamContinuationState.Read(JsonSerializer.SerializeToElement(state,WorkJson.Options));
        var thawed=restored.Replay.Single().Thaw();
        thawed.Result.GetRawText().ShouldBe(raw.RootElement.GetRawText());
        restored.RehydrateArchives([thawed]);
        JsonDocument.Parse(restored.Messages[2].Content!).RootElement.GetProperty("source").GetString().ShouldBe("界😀 café <tag>");
        thawed.ExecutionId.ShouldBeNull();
    }
}
