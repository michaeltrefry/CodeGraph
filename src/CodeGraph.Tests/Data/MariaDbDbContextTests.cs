using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Microsoft.EntityFrameworkCore;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbDbContextTests
{
    [Fact]
    public void Model_MapsStandaloneEntitiesToDonorMariaDbSchemaNames()
    {
        var options = new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(
                "Server=localhost;Database=codegraph;User ID=root;Password=test",
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;

        using var context = new CodeGraphDbContext(options);

        var repository = context.Model.FindEntityType(typeof(RepositoryEntity));
        repository.ShouldNotBeNull();
        repository.GetTableName().ShouldBe("repositories");
        repository.FindProperty(nameof(RepositoryEntity.SourceGroup))!
            .GetColumnName()
            .ShouldBe("gitlab_group");

        var batch = context.Model.FindEntityType(typeof(AnalysisBatchEntity));
        batch.ShouldNotBeNull();
        batch.FindProperty(nameof(AnalysisBatchEntity.ProviderBatchId))!
            .GetColumnName()
            .ShouldBe("anthropic_batch_id");
        batch.FindProperty(nameof(AnalysisBatchEntity.ExecutionMode))!
            .GetColumnName()
            .ShouldBe("execution_mode");
        batch.FindProperty(nameof(AnalysisBatchEntity.IncludeAllSource))!
            .GetColumnName()
            .ShouldBe("include_all_source");

        var batchRequest = context.Model.FindEntityType(typeof(AnalysisBatchRequestEntity));
        batchRequest.ShouldNotBeNull();
        batchRequest.FindProperty(nameof(AnalysisBatchRequestEntity.RequestPayloadJson))!
            .GetColumnName()
            .ShouldBe("request_payload_json");

        var wikiPage = context.Model.FindEntityType(typeof(WikiPageEntity));
        wikiPage.ShouldNotBeNull();
        wikiPage.GetTableName().ShouldBe("wiki_pages");
        wikiPage.FindProperty(nameof(WikiPageEntity.RawContent))!
            .GetColumnName()
            .ShouldBe("raw_content");
        wikiPage.GetIndexes()
            .Single(index => index.Properties.Select(p => p.Name).SequenceEqual(
            [
                nameof(WikiPageEntity.SectionId),
                nameof(WikiPageEntity.ParentId),
                nameof(WikiPageEntity.Slug)
            ]))
            .IsUnique
            .ShouldBeTrue();

        var exclusionRule = context.Model.FindEntityType(typeof(ExclusionRuleEntity));
        exclusionRule.ShouldNotBeNull();
        exclusionRule.GetTableName().ShouldBe("exclusion_rules");
        exclusionRule.FindProperty(nameof(ExclusionRuleEntity.TargetValue))!
            .GetColumnName()
            .ShouldBe("target_value");

        var assistantRun = context.Model.FindEntityType(typeof(AssistantRunEntity));
        assistantRun.ShouldNotBeNull();
        assistantRun.GetTableName().ShouldBe("assistant_runs");
        assistantRun.FindProperty(nameof(AssistantRunEntity.ExecutionStateJson))!
            .GetColumnName()
            .ShouldBe("execution_state_json");

        var mcpToken = context.Model.FindEntityType(typeof(McpPersonalAccessTokenEntity));
        mcpToken.ShouldNotBeNull();
        mcpToken.GetTableName().ShouldBe("mcp_personal_access_tokens");
        mcpToken.FindProperty(nameof(McpPersonalAccessTokenEntity.TokenPrefixValue))!
            .GetColumnName()
            .ShouldBe("token_prefix");

        var toolInvocation = context.Model.FindEntityType(typeof(McpToolInvocationEntity));
        toolInvocation.ShouldNotBeNull();
        toolInvocation.GetTableName().ShouldBe("mcp_tool_invocations");
        toolInvocation.FindProperty(nameof(McpToolInvocationEntity.DurationMs))!
            .GetColumnName()
            .ShouldBe("duration_ms");

        var usage = context.Model.FindEntityType(typeof(LlmUsageEntity));
        usage.ShouldNotBeNull();
        usage.GetTableName().ShouldBe("llm_usage");
        usage.FindProperty(nameof(LlmUsageEntity.TotalTokens))!
            .GetColumnName()
            .ShouldBe("total_tokens");
    }
}
