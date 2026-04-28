using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbLlmConfigRepositoryTests
{
    [Fact]
    public void LlmConfigRepository_ImplementsStandaloneLlmConfigContract()
    {
        typeof(ILlmConfigRepository).IsAssignableFrom(typeof(LlmConfigRepository)).ShouldBeTrue();
    }

    [Fact]
    public void LlmConfigKeys_RejectsUnknownKeys()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LlmConfigKeys.RequireKnownKey("analysis.defualt_provider"));
    }

    [Fact]
    public async Task LlmConfigRepository_ReadsSectionEntriesWithInMemoryProvider()
    {
        await using var context = new CodeGraphDbContext(new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseInMemoryDatabase($"llm-config-read-{Guid.NewGuid():N}")
            .Options);
        var store = new LlmConfigRepository(context, new PassthroughEncryptor());

        context.LlmConfig.AddRange(
            new LlmConfigEntryEntity
            {
                ConfigKey = LlmConfigKeys.ReviewDefaultProvider,
                ConfigValue = "openai",
                UpdatedAtUtc = DateTime.UtcNow
            },
            new LlmConfigEntryEntity
            {
                ConfigKey = LlmConfigKeys.ReviewMaxFindings,
                ConfigValue = "12",
                UpdatedAtUtc = DateTime.UtcNow
            });
        await context.SaveChangesAsync();

        var review = await store.GetReviewAsync();

        review.ShouldNotBeNull();
        review.DefaultProvider.ShouldBe("openai");
        review.MaxFindings.ShouldBe(12);
    }

    [Fact]
    public async Task LlmConfigRepository_RoundTripsSectionsWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_llm_config_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var storageOptions = new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"),
            EncryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
        };

        var runner = new MariaDbMigrationRunner(
            Options.Create(storageOptions),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            await using var context = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            var encryptor = new ConnectionStringEncryptor(Options.Create(storageOptions));
            var store = new LlmConfigRepository(context, encryptor);

            (await store.GetProviderAsync("anthropic")).ShouldBeNull();
            (await store.GetAnalysisAsync()).ShouldBeNull();
            (await store.GetReviewAsync()).ShouldBeNull();
            (await store.GetAssistantAsync()).ShouldBeNull();

            await store.SetProviderAsync(new LlmProviderWrite(
                ProviderKey: "anthropic",
                EndpointUrl: "https://api.anthropic.com/v1/messages",
                ApiVersion: "2023-06-01",
                Models: ["claude-sonnet-4-6", "claude-opus-4-6"],
                Token: null,
                UpdatedBy: "codex"));

            var provider = await store.GetProviderAsync("anthropic");
            provider.ShouldNotBeNull();
            provider.ProviderKey.ShouldBe("anthropic");
            provider.HasToken.ShouldBeFalse();
            provider.EndpointUrl.ShouldBe("https://api.anthropic.com/v1/messages");
            provider.ApiVersion.ShouldBe("2023-06-01");
            provider.Models.ShouldBe(["claude-sonnet-4-6", "claude-opus-4-6"]);
            provider.UpdatedBy.ShouldBe("codex");

            await store.SetAnalysisAsync(new LlmAnalysisWrite(
                DefaultProvider: "anthropic",
                DefaultModel: "claude-sonnet-4-6",
                MaxTokensPerAnalysis: 100000,
                MaxTokensPerSynthesis: 90000,
                MaxFileSizeKb: 2048,
                MaxParallelAnalyses: 3,
                MaxSourceChars: 64000,
                UpdatedBy: "codex"));

            var analysis = await store.GetAnalysisAsync();
            analysis.ShouldNotBeNull();
            analysis.DefaultProvider.ShouldBe("anthropic");
            analysis.DefaultModel.ShouldBe("claude-sonnet-4-6");
            analysis.MaxTokensPerAnalysis.ShouldBe(100000);
            analysis.MaxTokensPerSynthesis.ShouldBe(90000);
            analysis.MaxFileSizeKb.ShouldBe(2048);
            analysis.MaxParallelAnalyses.ShouldBe(3);
            analysis.MaxSourceChars.ShouldBe(64000);

            await store.SetReviewAsync(new LlmReviewWrite(
                DefaultProvider: "openai",
                DefaultModel: "gpt-5",
                MaxFilesToInspect: 30,
                MaxSourceCharsPerFile: 14000,
                MaxInspectionPasses: 5,
                MaxFindings: 25,
                UpdatedBy: "codex"));

            var review = await store.GetReviewAsync();
            review.ShouldNotBeNull();
            review.DefaultProvider.ShouldBe("openai");
            review.DefaultModel.ShouldBe("gpt-5");
            review.MaxFilesToInspect.ShouldBe(30);
            review.MaxSourceCharsPerFile.ShouldBe(14000);
            review.MaxInspectionPasses.ShouldBe(5);
            review.MaxFindings.ShouldBe(25);

            await store.SetAssistantAsync(new LlmAssistantWrite(
                DefaultProvider: "lmstudio",
                DefaultModel: "qwen3",
                MaxTokens: 8000,
                MaxTurns: 12,
                UpdatedBy: "codex"));

            var assistant = await store.GetAssistantAsync();
            assistant.ShouldNotBeNull();
            assistant.DefaultProvider.ShouldBe("lmstudio");
            assistant.DefaultModel.ShouldBe("qwen3");
            assistant.MaxTokens.ShouldBe(8000);
            assistant.MaxTurns.ShouldBe(12);

            await store.SetProviderAsync(new LlmProviderWrite(
                ProviderKey: "anthropic",
                EndpointUrl: "https://api.anthropic.com/v1/messages",
                ApiVersion: "2023-06-01",
                Models: ["claude-haiku-4-6", "claude-sonnet-4-6"],
                Token: null,
                UpdatedBy: "codex"));

            (await store.GetProviderAsync("anthropic"))!.Models
                .ShouldBe(["claude-haiku-4-6", "claude-sonnet-4-6"]);

            await store.SetProviderAsync(new LlmProviderWrite(
                ProviderKey: "anthropic",
                EndpointUrl: null,
                ApiVersion: null,
                Models: null,
                Token: new LlmProviderTokenWrite(LlmProviderTokenActionKind.Replace, "sk-ant-secret")));

            var encryptedToken = await context.LlmConfig.AsNoTracking()
                .Where(c => c.ConfigKey == LlmConfigKeys.ProviderTokenEncrypted("anthropic"))
                .Select(c => c.ConfigValue)
                .SingleAsync();
            encryptedToken.ShouldNotBe("sk-ant-secret");
            encryptedToken.ShouldStartWith("aes-gcm:v1:");

            (await store.GetProviderAsync("anthropic"))!.HasToken.ShouldBeTrue();
            (await store.GetProviderTokenAsync("anthropic")).ShouldBe("sk-ant-secret");

            await store.SetProviderAsync(new LlmProviderWrite(
                ProviderKey: "anthropic",
                EndpointUrl: null,
                ApiVersion: null,
                Models: null,
                Token: new LlmProviderTokenWrite(LlmProviderTokenActionKind.Preserve)));

            (await store.GetProviderTokenAsync("anthropic")).ShouldBe("sk-ant-secret");

            await store.SetProviderAsync(new LlmProviderWrite(
                ProviderKey: "anthropic",
                EndpointUrl: null,
                ApiVersion: null,
                Models: null,
                Token: new LlmProviderTokenWrite(LlmProviderTokenActionKind.Clear)));

            (await store.GetProviderAsync("anthropic"))!.HasToken.ShouldBeFalse();
            (await store.GetProviderTokenAsync("anthropic")).ShouldBeNull();

            await Should.ThrowAsync<ArgumentException>(() =>
                store.SetProviderAsync(new LlmProviderWrite(
                    ProviderKey: "anthropic",
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: null,
                    Token: new LlmProviderTokenWrite(LlmProviderTokenActionKind.Replace, " "))));

            await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
                store.SetProviderAsync(new LlmProviderWrite(
                    ProviderKey: "gemini",
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: null,
                    Token: null)));
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static DbContextOptions<CodeGraphDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(
                connectionString,
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;

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

    private sealed class PassthroughEncryptor : IAesEncryptor
    {
        public string Encrypt(string plainText) => plainText;

        public string Decrypt(string encrypted) => encrypted;
    }
}
