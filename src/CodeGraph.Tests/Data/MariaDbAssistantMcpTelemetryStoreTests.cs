using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Requests;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbAssistantMcpTelemetryStoreTests
{
    [Fact]
    public void MariaDbStores_ImplementAssistantMcpAndTelemetryContracts()
    {
        typeof(IAssistantRunStore).IsAssignableFrom(typeof(MySqlAssistantRunStore)).ShouldBeTrue();
        typeof(IMcpPersonalAccessTokenStore).IsAssignableFrom(typeof(MySqlMcpPersonalAccessTokenStore)).ShouldBeTrue();
        typeof(IMetricsEventStore).IsAssignableFrom(typeof(MySqlMetricsEventStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MariaDbStores_RoundTripAssistantMcpAndTelemetryDataWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_assistant_mcp_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });

        var runner = new MariaDbMigrationRunner(
            storageOptions,
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var assistantStore = new MySqlAssistantRunStore(storageOptions);
            var createdAt = DateTime.UtcNow.AddMinutes(-5);
            var createResult = await assistantStore.CreateAssistantRunAsync(new AssistantRunCreateRequest(
                ChatId: "chat-1",
                Username: "michael",
                Question: "What changed?",
                Context: "repo context",
                History: [],
                ProviderRequested: "openai",
                ModelRequested: "gpt-test",
                IdempotencyKey: "idem-1",
                RequestHash: "hash-1",
                CreatedAt: createdAt));

            createResult.Run.ShouldNotBeNull();
            var runId = createResult.Run.Id;

            var reused = await assistantStore.CreateAssistantRunAsync(new AssistantRunCreateRequest(
                "chat-1",
                "michael",
                "What changed?",
                "repo context",
                [],
                "openai",
                "gpt-test",
                "idem-1",
                "hash-1",
                createdAt));
            reused.ReusedExisting.ShouldBeTrue();

            (await assistantStore.TryClaimAssistantRunAsync(runId, "worker-1", DateTime.UtcNow.AddMinutes(5)))
                .ShouldNotBeNull();
            (await assistantStore.RenewAssistantRunLeaseAsync(runId, "worker-1", DateTime.UtcNow.AddMinutes(10)))
                .Renewed
                .ShouldBeTrue();

            await assistantStore.SaveAssistantRunProgressAsync(runId, new AssistantRunProgressUpdate(
            [
                new AssistantRunEventEntity
                {
                    RunId = runId,
                    Sequence = 1,
                    Type = "message_delta",
                    ContentJson = """{"text":"hello"}""",
                    CreatedAt = DateTime.UtcNow
                }
            ],
            ExecutionStateJson: """{"step":"thinking"}"""));

            (await assistantStore.GetAssistantRunEventsAsync(runId)).Single().Sequence.ShouldBe(1);

            await assistantStore.TransitionAssistantRunToTerminalAsync(runId, new AssistantRunTerminalUpdate(
                Status: "completed",
                Events:
                [
                    new AssistantRunEventEntity
                    {
                        RunId = runId,
                        Sequence = 2,
                        Type = "completed",
                        ContentJson = """{"ok":true}""",
                        CreatedAt = DateTime.UtcNow
                    }
                ],
                FinalAnswer: "Everything is wired.",
                CompletedAt: DateTime.UtcNow,
                ProviderUsed: "openai",
                ModelUsed: "gpt-test"));

            (await assistantStore.GetAssistantRunAsync(runId))!.Status.ShouldBe("completed");
            (await assistantStore.GetAssistantChatMessagesAsync("michael", "chat-1")).Count.ShouldBe(2);
            (await assistantStore.GetAssistantChatSummariesAsync("michael")).Single().ChatId.ShouldBe("chat-1");

            await assistantStore.AppendAssistantDebugExchangeAsync(new AssistantDebugExchangeEntity
            {
                RunId = runId,
                ChatId = "chat-1",
                Username = "michael",
                ExchangeIndex = 0,
                TurnIndex = 0,
                Provider = "openai",
                Model = "gpt-test",
                RequestBodyJson = "{}",
                RequestText = "prompt",
                CreatedAt = DateTime.UtcNow
            });
            (await assistantStore.GetAssistantDebugExchangesAsync(runId)).Single().RequestText.ShouldBe("prompt");

            await assistantStore.AppendAssistantDebugTraceAuditAsync(new AssistantDebugTraceAuditEntity
            {
                RunId = runId,
                ChatId = "chat-1",
                RunUsername = "michael",
                ViewedByUsername = "admin",
                ViewedAt = DateTime.UtcNow
            });

            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(
                    builder.ConnectionString,
                    ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;

            await using var context = new CodeGraphDbContext(dbOptions);
            var tokenStore = new MySqlMcpPersonalAccessTokenStore(context);
            var token = await tokenStore.CreateMcpPersonalAccessTokenAsync(new McpPersonalAccessTokenEntity
            {
                Username = "michael",
                TokenName = "local",
                TokenPrefixValue = "cgmcp_abc",
                TokenHash = "hash-token",
                LastFour = "abcd",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            });

            (await tokenStore.ListMcpPersonalAccessTokensAsync("michael")).Single().Id.ShouldBe(token.Id);
            (await tokenStore.GetMcpPersonalAccessTokenByHashAsync("hash-token"))!.Username.ShouldBe("michael");
            (await tokenStore.UpdateMcpPersonalAccessTokenLastUsedAsync(token.Id, DateTime.UtcNow, "127.0.0.1"))
                .ShouldBeTrue();
            (await tokenStore.RevokeMcpPersonalAccessTokenAsync("michael", token.Id, DateTime.UtcNow)).ShouldBeTrue();

            var metricsStore = new MySqlMetricsEventStore(storageOptions);
            await metricsStore.CreateLlmUsageAsync(new LlmUsageEntity
            {
                EventId = "usage-1",
                Username = "michael",
                Path = "/ask",
                Provider = "openai",
                Model = "gpt-test",
                InputTokens = 10,
                OutputTokens = 20,
                TotalTokens = 30,
                CreatedAt = DateTime.UtcNow
            });
            await metricsStore.CreateLlmUsageAsync(new LlmUsageEntity
            {
                EventId = "usage-1",
                Username = "michael",
                Path = "/ask",
                Provider = "openai",
                Model = "gpt-test",
                InputTokens = 10,
                OutputTokens = 20,
                TotalTokens = 30,
                CreatedAt = DateTime.UtcNow
            });

            await metricsStore.CreateMcpToolInvocationAsync(new McpToolInvocationEntity
            {
                EventId = "mcp-1",
                Username = "michael",
                TokenId = token.Id,
                ToolName = "search_graph",
                Success = true,
                DurationMs = 42,
                CreatedAt = DateTime.UtcNow
            });

            await using var conn = new MySqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM llm_usage WHERE event_id = 'usage-1'"))
                .ShouldBe(1);
            (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mcp_tool_invocations WHERE event_id = 'mcp-1'"))
                .ShouldBe(1);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = ""
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }
}
